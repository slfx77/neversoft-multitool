using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Decodes one stored mono wave from a validated N64 Sound Tools PTR/WBK
///     pair using ABI1/libultra audio-microcode runtime semantics, or resolves
///     one BFX effect from an audited ROM with its proven mixer rate, static
///     note pitch, and stored ALADPCM loop.
/// </summary>
public static class N64AudioDecodeCommand
{
    public const int MinimumSampleRate = 1;
    public const int MaximumSampleRate = 192_000;

    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a Sound Tools PTR file or supported big-endian .z64 ROM"
        };
        var waveOption = new Option<string?>("--wave")
        {
            Description = "Explicit paired WBK path (required for a standalone PTR input)"
        };
        var indexOption = new Option<int?>("--index")
        {
            Description = "Zero-based stored wave index (requires --sample-rate)"
        };
        var effectOption = new Option<int?>("--effect")
        {
            Description =
                "Zero-based BFX effect index from an audited .z64 ROM (derives exact mixer/static-pitch/loop metadata)"
        };
        var sampleRateOption = new Option<int?>("--sample-rate")
        {
            Description =
                "Caller-supplied WAV playback rate in Hz for --index (PTR/WBK contains no rate; policy range 1..192000)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output mono PCM16 WAV path",
            Required = true
        };

        var command = new Command(
            "n64-audio-decode",
            "Decode a stored N64 Sound Tools wave, or resolve an audited ROM BFX effect with exact static pitch and stored-loop metadata");
        command.Arguments.Add(inputArgument);
        command.Options.Add(waveOption);
        command.Options.Add(indexOption);
        command.Options.Add(effectOption);
        command.Options.Add(sampleRateOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(ExecuteSelection(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(waveOption),
                parseResult.GetValue(indexOption),
                parseResult.GetValue(effectOption),
                parseResult.GetValue(sampleRateOption),
                parseResult.GetValue(outputOption)!,
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input,
        string? wavePath,
        int waveIndex,
        int sampleRate,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        return ExecuteSelection(
            input,
            wavePath,
            waveIndex,
            effectIndex: null,
            sampleRate,
            outputPath,
            cancellationToken);
    }

    internal static int ExecuteSelection(
        string input,
        string? wavePath,
        int? waveIndex,
        int? effectIndex,
        int? sampleRate,
        string outputPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            if (waveIndex.HasValue == effectIndex.HasValue)
            {
                throw new InvalidDataException(
                    "select exactly one of --index (stored wave) or --effect (audited ROM BFX effect)");
            }

            if (effectIndex.HasValue)
            {
                if (!string.IsNullOrWhiteSpace(wavePath))
                    throw new InvalidDataException("--wave is not accepted with --effect");
                if (sampleRate.HasValue)
                {
                    throw new InvalidDataException(
                        "--sample-rate is not accepted with --effect; the audited ROM runtime and static pitch determine it");
                }

                return ExecuteEffect(
                    input,
                    effectIndex.Value,
                    outputPath,
                    cancellationToken);
            }

            if (!sampleRate.HasValue)
            {
                throw new InvalidDataException(
                    "--index requires --sample-rate because standalone PTR/WBK wave metadata contains no rate");
            }
            if (sampleRate.Value is < MinimumSampleRate or > MaximumSampleRate)
            {
                throw new InvalidDataException(
                    $"WAV playback rate {sampleRate.Value} Hz is outside the CLI policy range " +
                    $"{MinimumSampleRate}..{MaximumSampleRate}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var sources = N64SoundToolsInputResolver.Resolve(input, wavePath);
            cancellationToken.ThrowIfCancellationRequested();
            var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)waveIndex!.Value >= (uint)bank.PointerBank.Waves.Count)
            {
                throw new InvalidDataException(
                    $"stored wave index {waveIndex.Value} is outside 0..{bank.PointerBank.Waves.Count - 1}");
            }

            var wave = bank.PointerBank.Waves[waveIndex.Value];
            ValidateWavBounds(wave.WaveLength, sampleRate.Value);
            var encoded = sources.WaveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength);

            // Pairing, full PTR/WBK validation, index/range checks, output-size
            // preflight, and complete decode all precede destination creation.
            cancellationToken.ThrowIfCancellationRequested();
            var pcm = N64AdpcmDecoder.Decode(encoded, wave.Book);
            cancellationToken.ThrowIfCancellationRequested();
            RejectCanonicalSourcePath(input, wavePath, outputPath);
            WavWriter.WritePcm16(outputPath, sampleRate.Value, channels: 1, pcm);
            cancellationToken.ThrowIfCancellationRequested();

            AnsiConsole.MarkupLine(
                $"Decoded stored wave [green]{waveIndex.Value}[/] once with N64 ABI1/libultra " +
                "audio-microcode runtime semantics to " +
                $"[green]{pcm.Length}[/] mono PCM16 samples at caller-supplied WAV playback rate " +
                $"[green]{sampleRate.Value} Hz[/]: [green]{Markup.Escape(outputPath)}[/] " +
                "(PTR/WBK supplies no rate; loops, pitch, and cues were not applied)");
            return 0;
        }
        catch (Exception ex) when (ex is InvalidDataException
                                      or IOException
                                      or UnauthorizedAccessException
                                      or ArgumentException
                                      or NotSupportedException
                                      or OverflowException)
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
    }

    private static int ExecuteEffect(
        string input,
        int effectIndex,
        string outputPath,
        CancellationToken cancellationToken)
    {
        var classification = N64RomArchive.ClassifyRom(input);
        if (classification != "N64 ROM")
        {
            throw new InvalidDataException(classification ??
                "--effect requires a supported big-endian .z64 ROM, not a standalone audio file");
        }

        var rom = File.ReadAllBytes(input);
        cancellationToken.ThrowIfCancellationRequested();
        if (!N64RomArchive.TryReadMasterDirectory(rom, out _, out _, out var bootTable))
            throw new InvalidDataException("the ROM has no supported Edge of Reality master asset directory");
        var bootData = N64RomArchive.ExtractTable(rom, bootTable);
        var runtimeProfile = N64SoundToolsRuntimeProfileResolver.Resolve(rom, bootData);

        if (!N64AssetCarver.TryCarve(rom, out var assets))
            throw new InvalidDataException("the ROM has no supported Edge of Reality master asset directory");
        var waveSources = N64SoundToolsInputResolver.SelectCarvedPair(assets);
        var fxSources = N64SoundToolsFxInputResolver.SelectCarvedSources(assets);
        if (!waveSources.PointerData.AsSpan().SequenceEqual(fxSources.PointerData))
            throw new InvalidDataException("ROM wave and BFX selectors did not resolve the same PTR bank");

        var bank = N64SoundToolsBank.Parse(waveSources.PointerData, waveSources.WaveData);
        var playback = N64SoundToolsEffectPlaybackResolver.Resolve(
            fxSources.FxBank,
            bank.PointerBank,
            effectIndex,
            runtimeProfile.MixerProfile.AiFrequencyReturnHz);
        if (playback.VelocitySilencedByPitchLimit)
        {
            throw new InvalidDataException(
                $"Sound Tools effect {effectIndex} exceeds the runtime 2.0 pitch-ratio limit and is silenced");
        }
        if (playback.NearestWavRateHz is < MinimumSampleRate or > MaximumSampleRate)
        {
            throw new InvalidDataException(
                $"resolved WAV playback rate {playback.NearestWavRateHz} Hz is outside the CLI policy range " +
                $"{MinimumSampleRate}..{MaximumSampleRate}");
        }

        var wave = playback.PointerWave;
        var wavLoop = ResolveWavLoop(wave);
        ValidateWavBounds(wave.WaveLength, playback.NearestWavRateHz, wavLoop.HasValue);
        var encoded = waveSources.WaveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength);
        cancellationToken.ThrowIfCancellationRequested();
        var pcm = N64AdpcmDecoder.Decode(encoded, wave.Book);
        cancellationToken.ThrowIfCancellationRequested();
        RejectCanonicalSourcePath(input, wavePath: null, outputPath);
        WavWriter.WritePcm16(
            outputPath,
            playback.NearestWavRateHz,
            channels: 1,
            pcm,
            wavLoop);
        cancellationToken.ThrowIfCancellationRequested();

        var loopSummary = wavLoop is { } loop
            ? $"infinite stored ALADPCM loop {loop.StartSampleFrame}..{loop.EndSampleFrameInclusive} (RIFF inclusive end)"
            : "no stored ALADPCM loop";
        AnsiConsole.MarkupLine(
            $"Decoded BFX effect [green]{effectIndex}[/] via PTR wave " +
            $"[green]{playback.PointerWaveIndex}[/] to [green]{pcm.Length}[/] mono PCM16 samples at " +
            $"[green]{playback.NearestWavRateHz} Hz[/] WAV metadata " +
            $"(exact effective rate {playback.EffectiveStoredPcmRateHz:R} Hz; " +
            $"integer representation error {playback.WavRateRepresentationErrorHz:R} Hz), {loopSummary}: " +
            $"[green]{Markup.Escape(outputPath)}[/] " +
            "(stored samples were not resampled; BFX envelope, finite stop timing, and dynamic pitch/handle changes are not rendered)");
        return 0;
    }

    internal static Pcm16WavLoop? ResolveWavLoop(N64SoundToolsWaveDescriptor wave)
    {
        ArgumentNullException.ThrowIfNull(wave);
        if (wave.Loop is not { } loop)
            return null;
        if (loop.CountRaw != uint.MaxValue)
        {
            throw new InvalidDataException(
                $"PTR wave {wave.Index} has finite ALADPCM loop count 0x{loop.CountRaw:X8}; " +
                "its RIFF play-count conversion is not proven");
        }

        // Nintendo AL loops use an exclusive end sample; RIFF smpl uses an
        // inclusive end and play count zero means infinite.
        return new Pcm16WavLoop(loop.Start, checked(loop.End - 1), PlayCount: 0);
    }

    internal static void ValidateWavBounds(uint encodedLength, int sampleRate, bool hasLoop = false)
    {
        if (encodedLength % N64AdpcmDecoder.FrameSize != 0)
            throw new InvalidDataException("stored N64 ADPCM length is not an exact frame multiple");

        var frameCount = encodedLength / N64AdpcmDecoder.FrameSize;
        var sampleCount = checked((long)frameCount * N64AdpcmDecoder.SamplesPerFrame);
        var dataSize = checked(sampleCount * sizeof(short));
        var riffSize = checked(36L + dataSize + (hasLoop ? 68L : 0L));
        _ = checked(sampleRate * sizeof(short)); // mono PCM16 byte rate

        if (sampleCount > Array.MaxLength)
            throw new InvalidDataException("decoded sample count exceeds the runtime array limit");
        if (dataSize > Array.MaxLength || dataSize > int.MaxValue || riffSize > int.MaxValue)
            throw new InvalidDataException("decoded mono PCM16 wave exceeds the supported RIFF/array size");
    }

    private static void RejectCanonicalSourcePath(string input, string? wavePath, string outputPath)
    {
        // This guards normalized path aliases. Symlink/hard-link identity is a
        // separate filesystem-level overwrite policy.
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var canonicalOutput = Path.GetFullPath(outputPath);

        if (string.Equals(canonicalOutput, Path.GetFullPath(input), comparison) ||
            wavePath != null && string.Equals(canonicalOutput, Path.GetFullPath(wavePath), comparison))
        {
            throw new InvalidDataException(
                "output path resolves to the same canonical path as an input PTR/WBK/ROM source");
        }
    }
}
