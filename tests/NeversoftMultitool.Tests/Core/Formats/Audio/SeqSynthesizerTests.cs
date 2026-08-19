using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SeqSynthesizerTests(TestPaths paths)
{
    private const string ApocalypseBuild = "Apocalypse (1998-11-17, PSX - Final)";

    // ------------------------------------------------------------- SeqFile

    [Fact]
    public void Parse_ReadsHeaderRunningStatusAndTempo()
    {
        var seq = SeqFile.Parse(BuildSeq(
        [
            // delta 0: program change ch0 -> 5
            0x00, 0xC0, 0x05,
            // delta 0: note on ch0 note 60 vel 100
            0x00, 0x90, 0x3C, 0x64,
            // delta 0x60 running status: note off (vel 0) note 60
            0x60, 0x3C, 0x00,
            // delta 0: tempo meta 600000 us
            0x00, 0xFF, 0x51, 0x03, 0x09, 0x27, 0xC0,
            // delta 0x40: end of track
            0x40, 0xFF, 0x2F, 0x00
        ]));

        Assert.NotNull(seq);
        Assert.Equal(480, seq!.Resolution);
        Assert.Equal(500_000, seq.InitialTempoMicroseconds);
        Assert.Equal(
            [
                SeqEventType.ProgramChange,
                SeqEventType.NoteOn,
                SeqEventType.NoteOff,
                SeqEventType.Tempo,
                SeqEventType.EndOfTrack
            ],
            seq.Events.Select(static e => e.Type));
        Assert.Equal(0x60, seq.Events[2].Tick);
        Assert.Equal(600_000, seq.Events[3].Value);
        Assert.Equal(0x60 + 0x40, seq.Events[4].Tick);
    }

    [Fact]
    public void Parse_RejectsNonSeqAndTruncatedStreams()
    {
        Assert.Null(SeqFile.Parse("not a seq"u8));
        // Valid header, event stream cut mid-note.
        var truncated = BuildSeq([0x00, 0x90, 0x3C]);
        Assert.Null(SeqFile.Parse(truncated));
    }

    // ------------------------------------------------------- VabProgramSet

    [Fact]
    public void Parse_ReadsTonesForHighProgramSlots()
    {
        // The regression that silenced every Apocalypse song on first render:
        // programCount counts USED programs and the tone region packs used
        // slots in ascending order — Apocalypse's music banks use slots
        // 60..75, so slot-indexed tone reads (valid for SFX banks whose used
        // slots are 0..N-1) find nothing.
        var vab = VabProgramSet.Parse(BuildVab(programSlot: 60));

        Assert.NotNull(vab);
        Assert.Equal(1, vab!.ProgramCount);
        Assert.Empty(vab.Programs[0].Tones);
        var tone = Assert.Single(vab.Programs[60].Tones);
        Assert.Equal(60, tone.Centre);
        Assert.Equal(0, tone.MinNote);
        Assert.Equal(127, tone.MaxNote);
        Assert.Equal(1, tone.VagIndex);

        var pcm = vab.GetPcm(1);
        Assert.NotNull(pcm);
        Assert.True(pcm!.Loops);
        Assert.Equal(0, pcm.LoopStart);
        Assert.Equal(pcm.Samples.Length, pcm.LoopEnd);
    }

    // ------------------------------------------------------- Synthesizer

    [Fact]
    public void Render_OneNote_ProducesAudioAtTheCommandedPitch()
    {
        var vab = VabProgramSet.Parse(BuildVab(programSlot: 60))!;
        // One note a full octave above the tone centre, held one beat.
        var seq = SeqFile.Parse(BuildSeq(
        [
            0x00, 0xC0, 60,
            0x00, 0x90, 72, 0x7F,
            0x83, 0x60, 0x3C, 0x00, // delta 480 (one beat), running-status off
            0x00, 0x90, 72, 0x00, // duplicate off is harmless
            0x00, 0xFF, 0x2F, 0x00
        ]))!;

        var pcm = SeqSynthesizer.Render(seq, vab);

        Assert.NotNull(pcm);
        // 480 ticks at 500000 us/q = 0.5 s + the release tail.
        var seconds = pcm!.Length / 2.0 / SeqSynthesizer.OutputSampleRate;
        Assert.InRange(seconds, 2.4, 2.7);

        // Pitch check: the looped sample is a 56-sample square wave. Played
        // an octave above centre it doubles to a 28-sample period. Count
        // sign flips over the sustained region.
        var flips = 0;
        var start = (int)(0.1 * SeqSynthesizer.OutputSampleRate) * 2;
        var end = (int)(0.4 * SeqSynthesizer.OutputSampleRate) * 2;
        for (var i = start + 2; i < end; i += 2)
        {
            if (pcm[i] != 0 && pcm[i - 2] != 0 && (pcm[i] > 0) != (pcm[i - 2] > 0))
                flips++;
        }

        var measuredPeriod = (0.3 * SeqSynthesizer.OutputSampleRate) / (flips / 2.0);
        Assert.InRange(measuredPeriod, 26.5, 29.5);

        // And it must actually be audible.
        long energy = 0;
        for (var i = start; i < end; i += 2)
            energy += Math.Abs((int)pcm[i]);
        Assert.True(energy / ((end - start) / 2) > 500,
            "sustained region is near-silent");
    }

    [Fact]
    public void Render_NoteOutsideEveryToneRange_IsSilentButStillRenders()
    {
        var vab = VabProgramSet.Parse(BuildVab(programSlot: 60, minNote: 70, maxNote: 80))!;
        var seq = SeqFile.Parse(BuildSeq(
        [
            0x00, 0xC0, 60,
            0x00, 0x90, 60, 0x7F,
            0x83, 0x60, 0x3C, 0x00,
            0x00, 0xFF, 0x2F, 0x00
        ]))!;

        var pcm = SeqSynthesizer.Render(seq, vab);

        Assert.NotNull(pcm);
        Assert.All(pcm!, static sample => Assert.Equal(0, sample));
    }

    // ------------------------------------------------------------- Corpus

    [CorpusFact]
    public void Apocalypse_AllElevenSongsRenderAudibly()
    {
        var directory = paths.FindSampleFile(ApocalypseBuild, "backwall.seq");
        Assert.SkipWhen(directory == null, "Apocalypse fixtures not available");
        var cd = Path.GetDirectoryName(directory!)!;

        var rendered = 0;
        foreach (var seqPath in Directory.EnumerateFiles(cd, "*.seq"))
        {
            var seq = SeqFile.Parse(File.ReadAllBytes(seqPath));
            Assert.NotNull(seq);
            var vab = VabProgramSet.Parse(
                File.ReadAllBytes(Path.ChangeExtension(seqPath, ".vab")));
            Assert.NotNull(vab);

            var pcm = SeqSynthesizer.Render(seq!, vab!);
            Assert.NotNull(pcm);

            // Sample energy over the first 20 seconds — every song opens
            // with audible material.
            long energy = 0;
            var window = Math.Min(pcm!.Length, 20 * SeqSynthesizer.OutputSampleRate * 2);
            for (var i = 0; i < window; i++)
                energy += Math.Abs((int)pcm[i]);
            Assert.True(energy / window > 100,
                $"{Path.GetFileName(seqPath)} rendered near-silent");
            rendered++;
        }

        Assert.Equal(11, rendered);
    }

    [Fact]
    public void AFileThatOnlySharesTheExtensionIsSkippedNotFailed()
    {
        // `.seq` is shared with an unrelated Dreamcast "Sequencer File V1.0" container — 22 of
        // the corpus's 35 .seq files are that, not a PSY-Q song. Routing them into the SEQ
        // converter made an `audio` run over those builds report failures and exit 1, so a
        // structural non-match must report Skipped rather than an error.
        using var temp = new TempDirectory();
        var input = Path.Combine(temp.Path, "COST99.SEQ");
        File.WriteAllBytes(input, [.. "\0\0\0Sequencer File V1.0\0"u8, .. new byte[64]]);

        var result = SeqExtractor.ConvertToWav(input, Path.Combine(temp.Path, "out"));

        Assert.True(result.Skipped);
        Assert.False(result.Success);
        Assert.Contains("pQES", result.ErrorMessage!, StringComparison.Ordinal);
        // The diagnosis must be the magic, not a missing companion: these files have no .vab
        // sibling either, and reporting that first named the wrong cause.
        Assert.DoesNotContain("vab", result.ErrorMessage!, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "out")));
    }

    [CorpusFact]
    public void EveryCorpusSeqFileEitherRendersOrIsSkipped()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var psyQ = 0;
        var other = 0;
        foreach (var file in Directory
                     .EnumerateFiles(paths.SampleBuildsDir!, "*", SearchOption.AllDirectories)
                     .Where(static f => Path.GetExtension(f)
                         .Equals(".seq", StringComparison.OrdinalIgnoreCase)))
        {
            if (SeqFile.IsSeq(File.ReadAllBytes(file)))
                psyQ++;
            else
                other++;
        }

        // 13 Apocalypse songs plus THPS2 DC's SKATE.SEQ family; the other 22 are the Dreamcast
        // container that must never be counted as a conversion failure.
        Assert.Equal(13, psyQ);
        Assert.Equal(22, other);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-seq-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    // ------------------------------------------------------------ fixtures

    private static byte[] BuildSeq(byte[] events)
    {
        var header = new byte[]
        {
            (byte)'p', (byte)'Q', (byte)'E', (byte)'S',
            0, 0, 0, 1, // version
            0x01, 0xE0, // resolution 480
            0x07, 0xA1, 0x20, // tempo 500000
            0x04, 0x02 // 4/4
        };
        return [.. header, .. events];
    }

    /// <summary>
    ///     A minimal VAB: one program (at the given slot) with one tone over
    ///     the given note range, and one looping VAG holding two 28-sample
    ///     SPU-ADPCM blocks of a square wave (period 56 at unity pitch).
    /// </summary>
    private static byte[] BuildVab(int programSlot, byte minNote = 0, byte maxNote = 127)
    {
        var sizeTableOffset = 0x820 + 1 * 0x200;
        var vagData = BuildSquareVag();
        var total = sizeTableOffset + 512 + vagData.Length;
        var vab = new byte[total];

        BinaryPrimitives.WriteUInt32LittleEndian(vab, 0x56414270); // pBAV
        BinaryPrimitives.WriteUInt32LittleEndian(vab.AsSpan(4), 7); // version
        BinaryPrimitives.WriteUInt16LittleEndian(vab.AsSpan(0x12), 1); // programs used
        BinaryPrimitives.WriteUInt16LittleEndian(vab.AsSpan(0x14), 1); // tones
        BinaryPrimitives.WriteUInt16LittleEndian(vab.AsSpan(0x16), 1); // vags

        var programAttr = 0x20 + programSlot * 16;
        vab[programAttr] = 1; // tone count
        vab[programAttr + 1] = 127; // master volume

        var tone = 0x820; // first (and only) used tone set
        vab[tone + 2] = 127; // volume
        vab[tone + 3] = 64; // pan centre
        vab[tone + 4] = 60; // centre note
        vab[tone + 5] = 0; // shift
        vab[tone + 6] = minNote;
        vab[tone + 7] = maxNote;
        // ADSR: instant attack, no decay, full sustain, moderate release.
        BinaryPrimitives.WriteUInt16LittleEndian(vab.AsSpan(tone + 16), 0x000F);
        BinaryPrimitives.WriteUInt16LittleEndian(vab.AsSpan(tone + 18), 0x0010);
        BinaryPrimitives.WriteInt16LittleEndian(vab.AsSpan(tone + 22), 1); // VAG 1

        BinaryPrimitives.WriteUInt16LittleEndian(
            vab.AsSpan(sizeTableOffset + 2), (ushort)(vagData.Length / 8)); // entry 1
        vagData.CopyTo(vab.AsSpan(sizeTableOffset + 512));
        return vab;
    }

    private static byte[] BuildSquareVag()
    {
        // Two blocks, 28 samples each: +max then -max, shift 0 filter 0 so
        // nibbles decode directly. Block flags: loop-start on the first,
        // end+loop on the second -> a 56-sample looping square wave.
        var data = new byte[32];
        data[0] = 0x00;
        data[1] = SpuAdpcm.FlagLoopStart;
        for (var i = 2; i < 16; i++)
            data[i] = 0x77; // +7 nibbles
        data[16] = 0x00;
        data[17] = SpuAdpcm.FlagEnd | SpuAdpcm.FlagLoop;
        for (var i = 18; i < 32; i++)
            data[i] = 0x99; // -7 nibbles
        return data;
    }
}
