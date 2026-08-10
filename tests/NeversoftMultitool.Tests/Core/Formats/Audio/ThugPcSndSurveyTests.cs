using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

/// <summary>
///     THUG2 PC <c>.snd</c> files declare 16-bit mono PCM in their <c>fmt </c>
///     chunk and are not PCM at all. This pins the evidence so the format is not
///     re-implemented as a rename — the format backlog claimed exactly that for
///     a long time, and acting on it would emit 788 files of
///     white noise.
/// </summary>
public class ThugPcSndSurveyTests
{
    private const string WindowsBuild = "Tony Hawks Underground 2 (2004-10-4, Windows - Final)";

    private readonly TestPaths _paths = new();

    /// <summary>
    ///     nAvgBytesPerSec is not a byte rate: it equals 4 x the on-disk data size
    ///     (or that minus 2 when the sample count is odd), i.e. the DECODED byte
    ///     count. Four bytes out per byte in, at 16 bits per sample, means two
    ///     samples per byte — a 4-bit codec.
    /// </summary>
    [CorpusFact]
    public void Snd_AllPcSounds_AreFourBitCompressedNotPcm()
    {
        var files = _paths.FindSampleFiles(WindowsBuild, "*.snd").ToList();
        Assert.SkipWhen(files.Count == 0, "No .snd files in Sample/Builds");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!RiffWaveReader.TryRead(data, out var info))
            {
                offenders.Add($"{Path.GetFileName(file)}: not a RIFF/WAVE");
                continue;
            }

            var decodedBytes = 4 * info.DataLength;
            if (info.AvgBytesPerSec != decodedBytes && info.AvgBytesPerSec != decodedBytes - 2)
            {
                offenders.Add(
                    $"{Path.GetFileName(file)}: avg={info.AvgBytesPerSec}, data={info.DataLength} " +
                    $"(expected {decodedBytes} or {decodedBytes - 2})");
            }
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)));
    }

    /// <summary>
    ///     The header lies consistently: it always claims PCM, and it always
    ///     claims a block align of 2 (16-bit mono), which the payload contradicts.
    /// </summary>
    [CorpusFact]
    public void Snd_AllPcSounds_ClaimPcmInTheirHeader()
    {
        var files = _paths.FindSampleFiles(WindowsBuild, "*.snd").ToList();
        Assert.SkipWhen(files.Count == 0, "No .snd files in Sample/Builds");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            if (!RiffWaveReader.TryRead(File.ReadAllBytes(file), out var info))
                continue;

            if (info.FormatTag != 1 || info.BlockAlign != 2 || info.BitsPerSample != 16)
                offenders.Add($"{Path.GetFileName(file)}: tag={info.FormatTag} align={info.BlockAlign}");
        }

        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)));
    }

    /// <summary>
    ///     The executable requests one fewer sample for 253 files. Its decoder
    ///     therefore consumes only the low nibble of the last byte; the authored
    ///     high nibble is consistently zero padding.
    /// </summary>
    [CorpusFact]
    public void Snd_OddSampleStreamsHaveZeroUnusedHighNibble()
    {
        var files = _paths.FindSampleFiles(WindowsBuild, "*.snd").ToList();
        Assert.SkipWhen(files.Count == 0, "No .snd files in Sample/Builds");

        var oddFiles = 0;
        var offenders = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            if (!RiffWaveReader.TryRead(data, out var info)
                || info.AvgBytesPerSec != 4L * info.DataLength - 2)
                continue;

            oddFiles++;
            var lastPayloadByte = data[info.DataOffset + info.DataLength - 1];
            if ((lastPayloadByte & 0xF0) != 0)
                offenders.Add($"{Path.GetFileName(file)}: final byte 0x{lastPayloadByte:X2}");
        }

        Assert.Equal(253, oddFiles);
        Assert.True(offenders.Count == 0, string.Join("\n", offenders.Take(20)));
    }
}
