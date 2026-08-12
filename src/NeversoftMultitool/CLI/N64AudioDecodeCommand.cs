using System.CommandLine;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Decodes one stored mono wave from a validated N64 Sound Tools PTR/WBK
///     pair using ABI1/libultra audio-microcode runtime semantics. The caller
///     must supply WAV playback-rate metadata because PTR/WBK stores no rate.
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
        var indexOption = new Option<int>("--index")
        {
            Description = "Zero-based stored wave index",
            Required = true
        };
        var sampleRateOption = new Option<int>("--sample-rate")
        {
            Description =
                "Caller-supplied WAV playback rate in Hz (PTR/WBK contains no sample rate; policy range 1..192000)",
            Required = true
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output mono PCM16 WAV path",
            Required = true
        };

        var command = new Command(
            "n64-audio-decode",
            "Decode one stored mono wave once at a caller-supplied WAV rate with N64 ABI1/libultra audio-microcode runtime semantics (no loop, pitch, or cue processing)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(waveOption);
        command.Options.Add(indexOption);
        command.Options.Add(sampleRateOption);
        command.Options.Add(outputOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(waveOption),
                parseResult.GetValue(indexOption),
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
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        try
        {
            if (sampleRate is < MinimumSampleRate or > MaximumSampleRate)
            {
                throw new InvalidDataException(
                    $"WAV playback rate {sampleRate} Hz is outside the CLI policy range " +
                    $"{MinimumSampleRate}..{MaximumSampleRate}");
            }

            cancellationToken.ThrowIfCancellationRequested();

            var sources = N64SoundToolsInputResolver.Resolve(input, wavePath);
            cancellationToken.ThrowIfCancellationRequested();
            var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
            cancellationToken.ThrowIfCancellationRequested();
            if ((uint)waveIndex >= (uint)bank.PointerBank.Waves.Count)
            {
                throw new InvalidDataException(
                    $"stored wave index {waveIndex} is outside 0..{bank.PointerBank.Waves.Count - 1}");
            }

            var wave = bank.PointerBank.Waves[waveIndex];
            ValidateWavBounds(wave.WaveLength, sampleRate);
            var encoded = sources.WaveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength);

            // Pairing, full PTR/WBK validation, index/range checks, output-size
            // preflight, and complete decode all precede destination creation.
            cancellationToken.ThrowIfCancellationRequested();
            var pcm = N64AdpcmDecoder.Decode(encoded, wave.Book);
            cancellationToken.ThrowIfCancellationRequested();
            RejectCanonicalSourcePath(input, wavePath, outputPath);
            WavWriter.WritePcm16(outputPath, sampleRate, channels: 1, pcm);
            cancellationToken.ThrowIfCancellationRequested();

            AnsiConsole.MarkupLine(
                $"Decoded stored wave [green]{waveIndex}[/] once with N64 ABI1/libultra " +
                "audio-microcode runtime semantics to " +
                $"[green]{pcm.Length}[/] mono PCM16 samples at caller-supplied WAV playback rate " +
                $"[green]{sampleRate} Hz[/]: [green]{Markup.Escape(outputPath)}[/] " +
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

    internal static void ValidateWavBounds(uint encodedLength, int sampleRate)
    {
        if (encodedLength % N64AdpcmDecoder.FrameSize != 0)
            throw new InvalidDataException("stored N64 ADPCM length is not an exact frame multiple");

        var frameCount = encodedLength / N64AdpcmDecoder.FrameSize;
        var sampleCount = checked((long)frameCount * N64AdpcmDecoder.SamplesPerFrame);
        var dataSize = checked(sampleCount * sizeof(short));
        var riffSize = checked(36L + dataSize);
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
