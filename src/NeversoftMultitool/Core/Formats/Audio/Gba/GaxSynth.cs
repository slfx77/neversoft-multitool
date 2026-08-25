using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Audio.Gba;

/// <summary>
///     The GAX v1.99 synthesis engine, ported instruction-faithfully from the ROM's
///     own driver (sequencer 0x08036050, row interpreter 0x080362FC, note-on
///     0x080364CC, instrument set 0x08036508, perf list 0x08036550, per-tick slides
///     0x080366F0, envelope 0x08036A7C, vibrato 0x08036B44, window slide 0x08036B84,
///     voice render 0x08036790 and its ARM mixer stubs, output stage 0x08036FC4).
///
///     <para>This is a <b>frame machine at 59.7275 fps</b>, not a row-event renderer:
///     every frame runs sequencer → per-channel row/perf/envelope/slides → voice mix
///     → output, exactly as the hardware driver does. The reference reimplementation
///     it mirrors was validated dynamically — 3,871 live mixer voice-frame events
///     (two songs, both mix rates) matched exactly on wave address, resample step,
///     position, envelope, and volumes, and its audio scores 0.927 log-spectrogram
///     correlation against the emulator's real output — and this port reproduces
///     that reference's PCM byte-for-byte.</para>
///
///     <para>Engine tables are discovered from the ROM, not hardcoded: the rate
///     config sits at banner+0x50 (12 × {rateHz, timerPeriod}), the pitch table at
///     config+0x60 (<c>table[p] = 523.251 Hz × 2^(p/384) × 2048</c>, pitch unit =
///     1/32 semitone), and the vibrato sine directly after it. Waves are 1-based
///     records in the song's <c>sampleAddr</c> table — indexed directly, because the
///     contiguity scan that locates the sample <i>bank</i> undercounts the record
///     table (101 of 133 on THPS2). Note the order-list transpose byte <b>is</b>
///     applied — at mix time, per the current order position (an earlier static
///     reading claimed otherwise; the live captures settled it).</para>
/// </summary>
internal sealed class GaxSynth
{
    private const uint RomBase = 0x08000000;
    private const double FramesPerSecond = 59.7275;
    private const int PitchTableLength = 0xEF4;

    private readonly byte[] _rom;
    private readonly uint[] _pitchTable;
    private readonly sbyte[] _sineTable;
    private readonly int _mixRate;
    private readonly int _timerPeriod;
    private readonly int _samplesPerFrame;
    private readonly ulong _stepFactor;
    private readonly GbaGaxMusic.GaxSongHeader _header;
    private readonly int _orderStride;
    private readonly int _headerOffset;
    private readonly int _waveBaseOffset;
    private readonly int _masterVolume;
    private readonly Channel[] _channels;
    private readonly Sequencer _seq = new();

    /// <summary>The true hardware playback rate for the WAV (16777216 / timerPeriod).</summary>
    public int OutputRateHz => (int)Math.Round(16777216.0 / _timerPeriod);

    public GaxSynth(byte[] rom, GbaGaxMusic.GaxSongHeader header, int requestedRateHz)
    {
        _rom = rom;
        _header = header;
        _headerOffset = (int)(header.Address - RomBase);
        _orderStride = header.OrderLength * 4;

        // The song's wave table is the header's sampleAddr field at +0x10 — the same
        // wave-set base every header is located by.
        var sampleAddress = BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(_headerOffset + 0x10, 4));
        _waveBaseOffset = (int)(sampleAddress - RomBase);

        var banner = rom.AsSpan().IndexOf("GAX Sound Engine "u8);
        if (banner < 0)
            throw new InvalidDataException("No GAX engine banner in this ROM");

        // Rate config (banner+0x50): first of 12 entries with rate >= requested.
        var config = banner + 0x50;
        _mixRate = 0;
        for (var i = 0; i < 12; i++)
        {
            var rate = (int)ReadU32(config + i * 8);
            var period = (int)ReadU32(config + i * 8 + 4);
            _mixRate = rate;
            _timerPeriod = period;
            if (rate >= requestedRateHz)
                break;
        }

        _samplesPerFrame = (int)((long)_mixRate * 1000 / 59727);
        _stepFactor = (1UL << 32) / (uint)_mixRate;

        var pitchTableOffset = config + 0x60;
        _pitchTable = new uint[PitchTableLength];
        for (var i = 0; i < PitchTableLength; i++)
            _pitchTable[i] = ReadU32(pitchTableOffset + i * 4);

        var sineOffset = pitchTableOffset + 0x3BD0;
        _sineTable = new sbyte[64];
        for (var i = 0; i < 64; i++)
            _sineTable[i] = (sbyte)rom[sineOffset + i];

        _channels = new Channel[header.ChannelCount];
        for (var i = 0; i < _channels.Length; i++)
            _channels[i] = new Channel(i);

        // Output stage master (0x08036FC4). A music-only render uses the song's own
        // channel count (in-game the SFX system registers 2 more).
        var n = header.ChannelCount;
        _masterVolume = n == 1 ? 0x40 : 68 * (n + 8);
    }

    /// <summary>
    ///     Renders the song once through its order (plus a ~1 s ring-out) to mono
    ///     PCM16 at <see cref="OutputRateHz" />, capped at <paramref name="maxSeconds" />.
    /// </summary>
    public short[] Render(double maxSeconds)
    {
        var maxFrames = (int)(maxSeconds * FramesPerSecond);
        var output = new List<short>(maxFrames * _samplesPerFrame);
        var mixBuffer = new int[_samplesPerFrame];
        int? tailEnd = null;

        for (var frame = 0; frame < maxFrames; frame++)
        {
            SequencerTick();
            Array.Clear(mixBuffer);
            foreach (var ch in _channels)
                ChannelFrame(ch, mixBuffer);

            foreach (var sample in mixBuffer)
            {
                var v = (sample * _masterVolume) >> 10;
                output.Add((short)(Math.Clamp(v, -128, 127) << 8));
            }

            if (tailEnd == null && _seq.Ended != 0)
                tailEnd = frame + 60; // ~1 s ring-out after the order wraps
            if (tailEnd != null && frame >= tailEnd)
                break;
        }

        return [.. output];
    }

    // ---- sequencer tick (0x08036050) ----
    private void SequencerTick()
    {
        var s = _seq;
        if (s.Playing == 0 || s.Speed == 0)
            return;
        if (s.Tick == 0)
        {
            if (s.BreakFlag != 0)
            {
                s.BreakFlag = 0;
                s.Row = _header.RowsPerPattern;
            }
            else
            {
                s.Row++;
            }

            s.Tick = s.Speed - 1;
            if (s.Row >= _header.RowsPerPattern)
            {
                s.Row = 0;
                s.NewPattern = 1;
                s.OrderPos++;
                if (s.OrderPos >= _header.OrderLength)
                {
                    s.Ended = 1;
                    s.OrderPos = _header.LoopPoint;
                }
            }
            else
            {
                s.NewPattern = 0;
            }

            s.NewRow = 1;
        }
        else
        {
            s.Tick--;
            s.NewRow = 0;
        }
    }

    // The per-channel order list precedes the header at stride orderLen*4 — proven
    // equal to the engine's own per-channel data pointers for all 11 THPS2 songs.
    private int OrderEntryOffset(Channel ch, int orderPos) =>
        _headerOffset - _header.ChannelCount * _orderStride + ch.Index * _orderStride + orderPos * 4;

    // ---- channel frame update (0x0803622C) ----
    private void ChannelFrame(Channel ch, int[] buffer)
    {
        if (_seq.Playing != 0)
        {
            if (ch.Delay != 0)
            {
                ch.Delay--;
                if (ch.Delay == 0)
                    ProcessRow(ch, delayed: true);
            }

            if (_seq.Speed != 0 && _seq.NewRow != 0)
                ProcessRow(ch, delayed: false);
        }

        if (ch.Instrument != null && ch.PerfWait == 0)
        {
            if (ch.PerfSpeed != 0)
            {
                PerfStep(ch);
                ch.PerfWait = (byte)(ch.PerfSpeed - 1);
            }
        }
        else
        {
            ch.PerfWait--;
        }

        TickSlides(ch);
        RenderVoice(ch, buffer);
    }

    // ---- row interpreter (0x080362FC) ----
    private void ProcessRow(Channel ch, bool delayed)
    {
        ch.VolSlide = 0;
        ch.PatSlide = 0;
        ch.Delay = 0;
        int note = 0, instrument = 0, effect = 0, param = 0;

        if (!delayed)
        {
            if (_seq.NewPattern != 0)
            {
                var entry = OrderEntryOffset(ch, _seq.OrderPos);
                var patternOffset = BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(entry, 2));
                ch.PatternPtr = (int)(_header.NotesAddress - RomBase) + patternOffset;
                ch.Rest = 0;
                ch.Silent = _rom[ch.PatternPtr];
                ch.PatternPtr++;
            }

            if (ch.Silent != 0)
                return;
            if (ch.Rest != 0)
            {
                ch.Rest--;
                return;
            }

            var cmd = _rom[ch.PatternPtr];
            if (cmd == 0xFF)
            {
                ch.Rest = (byte)(_rom[ch.PatternPtr + 1] - 1);
                ch.PatternPtr += 2;
                return;
            }

            if ((cmd & 0x80) != 0)
            {
                var low = cmd & 0x7F;
                if (low == 0)
                {
                    ch.PatternPtr += 1;
                    return;
                }

                if (low <= 0x79)
                {
                    note = low;
                    instrument = _rom[ch.PatternPtr + 1];
                    ch.PatternPtr += 2;
                }
                else
                {
                    effect = _rom[ch.PatternPtr + 1];
                    param = _rom[ch.PatternPtr + 2];
                    ch.PatternPtr += 3;
                }
            }
            else
            {
                note = cmd;
                instrument = _rom[ch.PatternPtr + 1];
                effect = _rom[ch.PatternPtr + 2];
                param = _rom[ch.PatternPtr + 3];
                ch.PatternPtr += 4;
            }

            if (effect == 0x0E && (param >> 4) == 0x0D)
            {
                ch.Delay = (ushort)(param & 0x0F);
                ch.StoredNote = (byte)note;
                ch.StoredInstrument = (byte)instrument;
                return;
            }
        }
        else
        {
            note = ch.StoredNote;
            instrument = ch.StoredInstrument;
        }

        if (effect != 3)
            NoteOn(ch, note);
        SetInstrument(ch, instrument);

        switch (effect)
        {
            case 1:
                ch.PatSlide = (short)param;
                break;
            case 2:
                ch.PatSlide = (short)-param;
                break;
            case 3: // tone portamento
                if (param != 0)
                {
                    ch.PortaTarget = (short)((note - 2) << 5);
                    var diff = ch.PortaTarget - ch.NotePitch;
                    ch.PortaStep = (short)(diff / param); // signed divide truncates toward zero
                }

                break;
            case 0xA:
                ch.VolSlide = (byte)param;
                break;
            case 0xB:
                ch.VolSlide = (byte)-param;
                break;
            case 0xC:
                ch.Volume = (byte)param;
                break;
            case 0xD:
                _seq.BreakFlag = 1; // target row is stored but never read by v1.99
                break;
            case 0xF:
                _seq.Speed = param;
                _seq.Tick = param - 1;
                break;
        }
    }

    // ---- note-on (0x080364CC) ----
    private void NoteOn(Channel ch, int note)
    {
        if (note == 1)
        {
            if (ch.Instrument is { } instr && instr.EnvelopeSustain == 0xFF)
            {
                ch.PerfPitch = 0xFFFF;
                ch.PerfSlide = 0;
            }

            ch.Released = 1;
        }

        if (note > 1)
        {
            ch.NotePitch = (short)((note - 2) << 5);
            ch.Released = 0;
        }
    }

    // ---- instrument set (0x08036508) ----
    private void SetInstrument(Channel ch, int index)
    {
        if (index == 0)
            return;
        var instr = GetInstrument(index);
        ch.Instrument = instr;
        ch.EnvPos = 0;
        ch.Released = 0;
        ch.VibPhase = 0;
        if (instr == null)
            return;
        ch.VibDelayCount = instr.VibratoDelay;
        ch.PerfPos = 0;
        ch.PerfWait = 0;
        ch.PerfLoop = 0;
        ch.Volume = 0xFF;
        ch.PerfSpeed = instr.PerfSpeed;
    }

    // ---- perf-list step (0x08036550) ----
    private void PerfStep(Channel ch)
    {
        var instr = ch.Instrument!;
        if (ch.PerfPos >= instr.PerfLength)
        {
            ch.PerfSpeed = 0;
            return;
        }

        var entry = instr.PerfList + ch.PerfPos * 8;
        var note = _rom[entry];
        var fixedFlag = _rom[entry + 1];
        var waveSlot = _rom[entry + 2];
        var fx1 = BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(entry + 4, 2));
        var fx2 = BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(entry + 6, 2));
        ch.PerfPos++;

        if (note != 0)
        {
            ch.PerfPitch = (ushort)(short)((note - 2) << 5);
            ch.PerfFixed = fixedFlag;
            if (waveSlot != 0)
            {
                ch.WaveIndex = waveSlot - 1;
                ch.Position = 0;
                ch.Direction = 1;
                ch.PerfVolume = 0xFF;
                ch.WindowMode = 0;
                var rec = instr.Wave(ch.WaveIndex);
                if (rec.Slide != 0 && rec.LoopStart < rec.LoopEnd && rec.Length > 0
                    && rec.SlidePeriod != 0 && rec.SlideStep > 0)
                {
                    ch.WindowMode = 1;
                    ch.WindowStart = rec.Start;
                    ch.Position = (long)rec.Start << 11;
                    ch.WindowCount = (byte)rec.SlidePeriod;
                    ch.WindowDirection = 1;
                    if (ch.WindowStart + rec.Length > rec.LoopEnd)
                        ch.WindowDirection = -1;
                }
            }
        }

        ch.PerfVolSlide = 0;
        ch.PerfSlide = 0;
        foreach (var fx in (ReadOnlySpan<ushort>)[fx1, fx2])
        {
            var eff = fx >> 8;
            var p = fx & 0xFF;
            switch (eff)
            {
                case 1:
                    ch.PerfSlide = (short)p;
                    break;
                case 2:
                    ch.PerfSlide = (short)-p;
                    break;
                case 5: // jump (with loop count)
                    if (ch.PerfLoop != 0)
                    {
                        ch.PerfLoop--;
                        if (ch.PerfLoop == 0)
                            continue;
                    }

                    ch.PerfPos = p;
                    break;
                case 6: // set loop count
                    if (ch.PerfLoop == 0)
                        ch.PerfLoop = (byte)(p != 0 ? p + 1 : 0);
                    break;
                case 0xA:
                    ch.PerfVolSlide = (byte)p;
                    break;
                case 0xB:
                    ch.PerfVolSlide = (byte)-p;
                    break;
                case 0xC:
                    ch.PerfVolume = (byte)p;
                    break;
                case 0xF:
                    ch.PerfSpeed = (byte)p;
                    break;
            }
        }
    }

    // ---- envelope (0x08036A7C) ----
    private int EnvelopeEvaluate(Channel ch)
    {
        var instr = ch.Instrument!;
        var env = instr.EnvelopeAddress;
        var count = _rom[env];
        var sustain = _rom[env + 1];
        var loopStart = _rom[env + 2];
        var loopEnd = _rom[env + 3];

        int PointTime(int i) => BinaryPrimitives.ReadUInt16LittleEndian(_rom.AsSpan(env + 4 + i * 8, 2));
        int PointSlope(int i) => BinaryPrimitives.ReadInt16LittleEndian(_rom.AsSpan(env + 6 + i * 8, 2));
        int PointValue(int i) => _rom[env + 8 + i * 8];

        var pos = ch.EnvPos;
        ch.EnvPos = pos + 1;
        if (sustain != 0xFF && ch.Released == 0 && pos == PointTime(sustain))
            ch.EnvPos = pos; // hold

        var last = count - 1;
        if (pos >= PointTime(last))
        {
            if (PointValue(last) == 0 && (loopEnd == 0xFF || loopEnd < last))
            {
                ch.PerfPitch = 0xFFFF;
                ch.PerfSlide = 0;
            }

            ch.EnvPos = pos; // clamp
        }

        if (ch.Released == 0 && loopStart != 0xFF && loopEnd != 0xFF && pos == PointTime(loopEnd))
            ch.EnvPos = PointTime(loopStart);

        var i = 0;
        while (PointTime(i) < pos)
            i++;
        if (PointTime(i) == pos)
            return PointValue(i);
        var prevTime = PointTime(i - 1);
        return (PointValue(i - 1) + ((pos - prevTime) * PointSlope(i) >> 8)) & 0xFF;
    }

    // ---- vibrato (0x08036B44) ----
    private void Vibrato(Channel ch)
    {
        var instr = ch.Instrument!;
        if (instr.VibratoDepth == 0)
        {
            ch.VibValue = 0;
            return;
        }

        if (ch.VibDelayCount == 0)
            ch.VibPhase = (byte)((ch.VibPhase + instr.VibratoSpeed) & 0x3F);
        else
            ch.VibDelayCount--;
        ch.VibValue = (short)(_sineTable[ch.VibPhase] * instr.VibratoDepth >> 8);
    }

    // ---- window slide (0x08036B84) ----
    private void WindowSlide(Channel ch)
    {
        if (ch.WindowMode == 0)
            return;
        ch.WindowCount--;
        if (ch.WindowCount != 0)
            return;
        var old = ch.WindowStart;
        var rec = ch.Instrument!.Wave(ch.WaveIndex);
        ch.WindowCount = (byte)rec.SlidePeriod;
        if (ch.WindowDirection > 0)
        {
            ch.WindowStart += rec.SlideStep;
            if (ch.WindowStart + rec.Length > rec.LoopEnd)
            {
                ch.WindowStart -= 2 * rec.SlideStep;
                ch.WindowDirection = -1;
            }
        }
        else
        {
            ch.WindowStart -= rec.SlideStep;
            if (ch.WindowStart < rec.LoopStart)
            {
                ch.WindowStart += 2 * rec.SlideStep;
                ch.WindowDirection = 1;
            }
        }

        ch.Position += (long)(ch.WindowStart - old) << 11;
    }

    // ---- per-tick slides (0x080366F0) ----
    private void TickSlides(Channel ch)
    {
        if (ch.Instrument != null)
        {
            ch.EnvValue = (byte)EnvelopeEvaluate(ch);
            Vibrato(ch);
            WindowSlide(ch);
        }

        ch.Volume = (byte)Math.Clamp(ch.Volume + (sbyte)ch.VolSlide, 0, 0xFF);
        ch.PerfVolume = (byte)Math.Clamp(ch.PerfVolume + (sbyte)ch.PerfVolSlide, 0, 0xFF);
        ch.NotePitch = (short)(ch.NotePitch + ch.PatSlide);
        ch.PerfPitch = (ushort)(ch.PerfPitch + ch.PerfSlide);
        if (ch.PortaStep != 0)
        {
            var before = (ch.PortaTarget - ch.NotePitch) < 0;
            ch.NotePitch = (short)(ch.NotePitch + ch.PortaStep);
            var after = (ch.PortaTarget - ch.NotePitch) < 0;
            if (before != after)
            {
                ch.PortaStep = 0;
                ch.NotePitch = ch.PortaTarget;
                ch.PortaTarget = 0;
            }
        }
    }

    // ---- voice render (0x08036790 + ARM mixer stubs) ----
    private void RenderVoice(Channel ch, int[] buffer)
    {
        var instr = ch.Instrument;
        if (instr == null || (short)ch.PerfPitch == -1 || ch.WaveIndex > 3)
            return;
        var sampleNumber = instr.SampleIndex(ch.WaveIndex);
        var waveAddress = ReadU32(_waveBaseOffset + sampleNumber * 8);
        var waveSize = (int)ReadU32(_waveBaseOffset + sampleNumber * 8 + 4);
        if (waveAddress == 0)
            return;
        var rec = instr.Wave(ch.WaveIndex);

        var pitch = (short)ch.PerfPitch + ch.VibValue;
        if (ch.PerfFixed == 0)
        {
            pitch += ch.NotePitch;
            var entry = OrderEntryOffset(ch, _seq.OrderPos & 0xFFFF);
            pitch += (sbyte)_rom[entry + 2] << 5; // order transpose, applied at mix time
        }

        pitch += rec.Finetune;
        if ((uint)pitch > 0xEF3) // unsigned clamp: negatives also land on 0xEF3
            pitch = 0xEF3;
        var step = (long)(_pitchTable[pitch] * _stepFactor >> 32);

        var volume = 0x100;
        if (ch.EnvValue != 0xFF)
            volume = ch.EnvValue;
        if (ch.PerfVolume != 0xFF)
            volume = volume * ch.PerfVolume >> 8;
        if (ch.Volume != 0xFF)
            volume = volume * ch.Volume >> 8;
        if (ch.ApiVolume != 0xFF)
            volume = volume * ch.ApiVolume >> 8;
        if (_seq.SongVolume != 0xFF)
            volume = volume * _seq.SongVolume >> 8;

        var hasForwardLoop = rec.Slide == 0 && rec.LoopStart < rec.LoopEnd;
        var waveOffset = (int)(waveAddress - RomBase);
        var pos = ch.Position;
        var written = 0;

        while (written < _samplesPerFrame)
        {
            if (ch.Direction > 0)
            {
                long end = hasForwardLoop ? rec.LoopEnd
                    : ch.WindowMode != 0 ? ch.WindowStart + rec.Length
                    : waveSize;
                var endFp = end << 11;
                if (pos < endFp && step > 0)
                {
                    while (written < _samplesPerFrame && pos < endFp)
                    {
                        buffer[written] += (sbyte)_rom[waveOffset + (int)(pos >> 11)] * volume >> 8;
                        pos += step;
                        written++;
                    }
                }

                if (written >= _samplesPerFrame)
                    break;
                if (hasForwardLoop)
                {
                    if (rec.PingPong != 0)
                    {
                        ch.Direction = -2; // the engine's ~1 = 0xFE
                        pos -= 2 * step;
                    }
                    else
                    {
                        pos -= (long)(rec.LoopEnd - rec.LoopStart) << 11;
                    }
                }
                else if (ch.WindowMode != 0)
                {
                    pos -= (long)rec.Length << 11;
                }
                else
                {
                    ch.PerfPitch = 0xFFFF; // one-shot: the voice dies
                    ch.PerfSlide = 0;
                    break;
                }
            }
            else
            {
                var endFp = (long)rec.LoopStart << 11;
                if (pos > endFp && step > 0)
                {
                    while (written < _samplesPerFrame && pos > endFp)
                    {
                        buffer[written] += (sbyte)_rom[waveOffset + (int)(pos >> 11)] * volume >> 8;
                        pos -= step;
                        written++;
                    }
                }

                if (written >= _samplesPerFrame)
                    break;
                ch.Direction = 1; // flip back to forward
                pos += 2 * step;
            }
        }

        ch.Position = pos;
    }

    private uint ReadU32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(_rom.AsSpan(offset, 4));

    private readonly Dictionary<int, InstrumentRef?> _instrumentCache = [];

    private InstrumentRef? GetInstrument(int index)
    {
        if (_instrumentCache.TryGetValue(index, out var cached))
            return cached;
        var pointer = ReadU32((int)(_header.InstrumentAddress - RomBase) + index * 4);
        InstrumentRef? instr = null;
        if (pointer >= RomBase && pointer < RomBase + (uint)_rom.Length && _rom[pointer - RomBase] == 0)
            instr = new InstrumentRef(_rom, (int)(pointer - RomBase));
        _instrumentCache[index] = instr;
        return instr;
    }

    /// <summary>An instrument, read in place from the ROM (offsets per the port spec).</summary>
    private sealed class InstrumentRef(byte[] rom, int offset)
    {
        public byte VibratoDelay => rom[offset + 8];
        public byte VibratoDepth => rom[offset + 9];
        public byte VibratoSpeed => rom[offset + 10];
        public int EnvelopeAddress => (int)(BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(offset + 0x7C, 4)) - RomBase);
        public byte EnvelopeSustain => rom[EnvelopeAddress + 1];
        public byte PerfSpeed => rom[offset + 0x84];
        public byte PerfLength => rom[offset + 0x85];
        public int PerfList => (int)(BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(offset + 0x88, 4)) - RomBase);

        public int SampleIndex(int slot) => rom[offset + 1 + slot];

        public WaveRecord Wave(int slot)
        {
            var at = offset + 0x0C + slot * 28;
            return new WaveRecord(
                rom[at],
                rom[at + 1],
                BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(at + 4, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(at + 8, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(at + 12, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(at + 16, 4)),
                BinaryPrimitives.ReadUInt32LittleEndian(rom.AsSpan(at + 20, 4)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.AsSpan(at + 24, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.AsSpan(at + 26, 2)));
        }
    }

    private readonly record struct WaveRecord(
        byte Slide, byte PingPong, uint Start, uint LoopStart, uint LoopEnd,
        uint Length, uint SlideStep, ushort SlidePeriod, short Finetune);

    private sealed class Sequencer
    {
        public int OrderPos = -1;
        public int Row = 20000;
        public int Playing = 1;
        public int Speed = 6;
        public int Tick;
        public int NewRow;
        public int NewPattern;
        public byte SongVolume = 0xFF;
        public int Ended;
        public int BreakFlag;
    }

    private sealed class Channel(int index)
    {
        public readonly int Index = index;

        public byte Silent;
        public byte Rest;
        public int WaveIndex;
        public int Direction = 1;      // ping-pong: >0 forward
        public byte WindowMode;
        public int WindowDirection = 1;
        public byte WindowCount;
        public byte Volume = 0xFF;
        public byte EnvValue = 0xFF;
        public byte PerfVolume = 0xFF;
        public byte ApiVolume = 0xFF;  // the game's music-volume API; full here
        public byte VolSlide;
        public byte PerfVolSlide;
        public byte PerfSpeed;
        public byte PerfWait;
        public byte PerfLoop;
        public byte PerfFixed;
        public byte Released;
        public byte VibDelayCount;
        public short NotePitch;
        public short PatSlide;
        public ushort PerfPitch = 0xFFFF; // 0xFFFF = voice dead
        public short PerfSlide;
        public short VibValue;
        public short PortaTarget;
        public short PortaStep;
        public ushort Delay;
        public int PerfPos;
        public int EnvPos;
        public byte VibPhase;
        public InstrumentRef? Instrument;
        public int PatternPtr;
        public long Position;          // 21.11 fixed
        public uint WindowStart;
        public byte StoredNote;
        public byte StoredInstrument;
    }
}
