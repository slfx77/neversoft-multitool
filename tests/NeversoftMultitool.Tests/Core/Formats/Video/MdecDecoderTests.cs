using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

public class MdecDecoderTests(TestPaths paths)
{
    private const string KnownCorruptSm2E5M6FrameSha256 =
        "ec1ec8ae4e8927ef009c3f158357904e7f0acc1f7bef8c20121a4d2dcc957f34";

    private string? FindStrFile(string buildPattern, string fileName)
    {
        if (!paths.HasSampleBuilds) return null;
        var buildDir = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains(buildPattern, StringComparison.OrdinalIgnoreCase));
        if (buildDir == null) return null;
        return Directory.GetFiles(buildDir, "*", SearchOption.AllDirectories)
            .FirstOrDefault(f => Path.GetFileName(f).Equals(fileName, StringComparison.OrdinalIgnoreCase));
    }

    private string[] GetAllStrFiles()
    {
        if (!paths.HasSampleBuilds) return [];
        return Directory.GetDirectories(paths.SampleBuildsDir!)
            .SelectMany(build =>
            {
                return Directory.GetFiles(build, "*.str", SearchOption.AllDirectories)
                    .Concat(Directory.GetFiles(build, "*.STR", SearchOption.AllDirectories));
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(f =>
            {
                // Skip AFS archives (DC SPEECH.STR)
                try
                {
                    var header = new byte[4];
                    using var fs = File.OpenRead(f);
                    if (fs.Read(header, 0, 4) < 4) return false;
                    return !(header[0] == 'A' && header[1] == 'F' && header[2] == 'S' && header[3] == 0);
                }
                catch
                {
                    return false;
                }
            })
            .ToArray();
    }

    // ── StrDemuxer Tests ───────────────────────────────────────────────

    [CorpusFact]
    public void IsStrFile_ValidStrFile_ReturnsTrue()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        file ??= FindStrFile("Spider-Man (2000-9-1", "ATTRACT.STR");
        Assert.SkipWhen(file == null, "No STR file found");

        var data = File.ReadAllBytes(file!);
        Assert.True(StrDemuxer.IsStrFile(data));
    }

    [CorpusFact]
    public void IsStrFile_AfsArchive_ReturnsFalse()
    {
        // DC SPEECH.STR is actually an AFS archive
        if (!paths.HasSampleBuilds) Assert.Skip("Sample builds not available");

        var dcBuild = Directory.GetDirectories(paths.SampleBuildsDir!)
            .FirstOrDefault(d => Path.GetFileName(d).Contains("DC", StringComparison.OrdinalIgnoreCase));
        Assert.SkipWhen(dcBuild == null, "No DC build found");

        var speechStr = Directory.GetFiles(dcBuild!, "SPEECH.STR",
            SearchOption.AllDirectories).FirstOrDefault();

        if (speechStr == null) Assert.Skip("DC SPEECH.STR not found");

        var data = File.ReadAllBytes(speechStr);
        Assert.False(StrDemuxer.IsStrFile(data));
    }

    [Fact]
    public void IsStrFile_TooSmall_ReturnsFalse()
    {
        Assert.False(StrDemuxer.IsStrFile(new byte[100]));
    }

    [Fact]
    public void IsStrFile_WrongAlignment_ReturnsFalse()
    {
        Assert.False(StrDemuxer.IsStrFile(new byte[2337]));
    }

    [CorpusFact]
    public void EnumerateFrames_ApocalypseIntro_HasFrames()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var frames = StrDemuxer.EnumerateFrames(data).Take(5).ToList();

        Assert.NotEmpty(frames);
        Assert.Equal(320, frames[0].Width);
        Assert.Equal(240, frames[0].Height);
        Assert.True(frames[0].Data.Length > 0);
    }

    [Fact]
    public void EnumerateFrames_Form1Chunks_ExcludeEdcAndEccTail()
    {
        const int sectorSize = 2336;
        const int subheaderSize = 8;
        const int videoHeaderSize = 32;
        const int form1PieceSize = 2016;
        var data = new byte[sectorSize * 2];

        for (ushort chunkIndex = 0; chunkIndex < 2; chunkIndex++)
        {
            var sectorOffset = chunkIndex * sectorSize;
            data[sectorOffset + 2] = 0x48; // Mode-2 Form-1 video sector.
            var headerOffset = sectorOffset + subheaderSize;
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset, 2), (ushort)0x0160);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 2, 2), (ushort)0x8001);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 4, 2), chunkIndex);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 6, 2), (ushort)2);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 8, 4), 7u);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 12, 4), (uint)(form1PieceSize * 2));
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 16, 2), (ushort)16);
            BitConverter.TryWriteBytes(data.AsSpan(headerOffset + 18, 2), (ushort)16);

            var pieceStart = headerOffset + videoHeaderSize;
            data.AsSpan(pieceStart, form1PieceSize).Fill((byte)(0x11 + chunkIndex));
            data.AsSpan(pieceStart + form1PieceSize, sectorSize - subheaderSize - videoHeaderSize - form1PieceSize)
                .Fill(0xEE);
        }

        var frame = Assert.Single(StrDemuxer.EnumerateFrames(data));

        Assert.Equal(form1PieceSize * 2, frame.Data.Length);
        Assert.All(frame.Data[..form1PieceSize], value => Assert.Equal((byte)0x11, value));
        Assert.All(frame.Data[form1PieceSize..], value => Assert.Equal((byte)0x12, value));
        Assert.DoesNotContain((byte)0xEE, frame.Data);
        Assert.Equal(1, StrDemuxer.CountFrames(data));
    }

    [CorpusFact]
    public void CountFrames_ApocalypseIntroCountsCompleteFramesRatherThanHeaderMaximum()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var count = StrDemuxer.CountFrames(data);

        // This stream numbers its 1,960 frames 1..1,960. max+1 therefore
        // produced 1,961 even on clean input.
        Assert.Equal(1960, count);
    }

    [CorpusFact]
    public void HasAudio_TypicalStrFile_ReturnsTrue()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        Assert.True(StrDemuxer.HasAudio(data));
    }

    [CorpusFact]
    public void ExtractAudioSectors_ReturnsAlignedData()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var audio = StrDemuxer.ExtractAudioSectors(data);

        Assert.True(audio.Length > 0, "Expected audio sectors");
        Assert.Equal(0, audio.Length % 2336); // Must be sector-aligned
    }

    // ── MdecDecoder Tests ──────────────────────────────────────────────

    [CorpusFact]
    public void DecodeFrame_FirstFrame_ProducesNonZeroRgb()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        file ??= FindStrFile("Spider-Man (2000-9-1", "ATTRACT.STR");
        Assert.SkipWhen(file == null, "No STR file found for decode test");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).First();

        var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);

        Assert.Equal(frame.Width * frame.Height * 3, rgb.Length);

        // Verify not all black (at least some non-zero pixels)
        var nonZero = 0;
        for (var i = 0; i < rgb.Length; i++)
            if (rgb[i] != 0)
                nonZero++;

        Assert.True(nonZero > rgb.Length / 10,
            $"Expected >10% non-zero pixels, got {nonZero}/{rgb.Length} ({100.0 * nonZero / rgb.Length:F1}%)");
    }

    [CorpusFact]
    public void DecodeFrame_OutputDimensions_MatchInput()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).First();

        var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);

        // RGB24: 3 bytes per pixel
        Assert.Equal(320 * 240 * 3, rgb.Length);
    }

    [CorpusFact]
    public void DecodeFrame_MultipleFrames_AllDecode()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var errors = new List<string>();
        var decoded = 0;

        foreach (var frame in StrDemuxer.EnumerateFrames(data).Take(30))
        {
            try
            {
                var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);
                Assert.Equal(frame.Width * frame.Height * 3, rgb.Length);
                decoded++;
            }
            catch (Exception ex)
            {
                errors.Add($"Frame {frame.FrameNumber}: {ex.Message}");
            }
        }

        Assert.True(decoded > 0, "No frames decoded");
        Assert.True(errors.Count == 0,
            $"Failed to decode {errors.Count}/{decoded + errors.Count} frames:\n{string.Join("\n", errors)}");
    }

    [CorpusFact]
    public void DecodeFrame_ApocalypseIntroFrame101_PinsJpsxdecFramingAndRgbRegression()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).Single(candidate => candidate.FrameNumber == 101);

        Assert.Equal(18144, frame.Data.Length);
        Assert.Equal(
            "0ad3d0dc67fa62f1bad45d804008b2531f459a36165beeb7e84acb3820bcd3c5",
            Convert.ToHexStringLower(SHA256.HashData(frame.Data)));

        var rgb = MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height);
        Assert.Equal(
            "343518ba1c192a7fe860ac7cc94ced46c683356ebf9caa4a53ba719511091147",
            Convert.ToHexStringLower(SHA256.HashData(rgb)));
    }

    [Fact]
    public void DecodeFrame_TruncatedBitstream_ThrowsInsteadOfReturningPartialImage()
    {
        var frame = new byte[9];
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x3800);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), (ushort)2);

        Assert.Throws<InvalidDataException>(() => MdecDecoder.DecodeFrame(frame, 16, 16));
    }

    [Theory]
    [InlineData(0, 16, "width")]
    [InlineData(16, 0, "height")]
    public void DecodeFrame_ZeroWidthOrHeight_ThrowsArgumentOutOfRange(
        int width, int height, string parameterName)
    {
        var frame = new byte[8];
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x3800);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), (ushort)2);

        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            MdecDecoder.DecodeFrame(frame, width, height));
        Assert.Equal(parameterName, error.ParamName);
    }

    [Fact]
    public void StrPreviewFrameDecoder_TruncatedFrame_ReturnsOpaqueBlackBgra()
    {
        var frame = new byte[9];
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x3800);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), (ushort)2);

        var bgra = StrPreviewFrameDecoder.DecodeBgra8OrBlack(frame, 16, 16);

        Assert.Equal(16 * 16 * 4, bgra.Length);
        Assert.All(bgra.Chunk(4), pixel => Assert.Equal([0, 0, 0, 0xFF], pixel));
    }

    [Fact]
    public void DecodeFrame_Version3_ReportsUnsupportedBitstream()
    {
        var frame = new byte[8];
        BitConverter.TryWriteBytes(frame.AsSpan(2, 2), (ushort)0x3800);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 2), (ushort)1);
        BitConverter.TryWriteBytes(frame.AsSpan(6, 2), (ushort)3);

        var error = Assert.Throws<InvalidDataException>(() => MdecDecoder.DecodeFrame(frame, 16, 16));
        Assert.Contains("only version 2 is supported", error.Message);
    }

    [CorpusFact]
    public void DecodeFrame_Sm2FinalE5M6HeaderFrame2_IsRejectedByBothDecoders()
    {
        var file = FindStrFile("Spider-Man 2 - Enter Electro (2001-8-15", "E5M6.STR");
        Assert.SkipWhen(file == null, "SM2 Final E5M6.STR not found");

        var data = File.ReadAllBytes(file!);
        var frame = StrDemuxer.EnumerateFrames(data).First();

        // Corrupt chunk headers prevent frame 1 from assembling as a complete
        // frame, so the first frame the demuxer can yield is header frame 2.
        Assert.Equal(2, frame.FrameNumber);
        Assert.Equal(KnownCorruptSm2E5M6FrameSha256,
            Convert.ToHexStringLower(SHA256.HashData(frame.Data)));
        var error = Assert.Throws<InvalidDataException>(() =>
            MdecDecoder.DecodeFrame(frame.Data, frame.Width, frame.Height));
        Assert.Contains("macroblock (16, 5), block 3, bit 64832", error.Message);
        Assert.Equal(551, StrDemuxer.CountFrames(data));
    }

    // ── StrProbeResult Tests ───────────────────────────────────────────

    [CorpusFact]
    public void Probe_ValidFile_ReturnsMetadata()
    {
        var file = FindStrFile("Apocalypse", "INTRO.STR");
        Assert.SkipWhen(file == null, "Apocalypse INTRO.STR not found");

        var result = StrConverter.Probe(file!);

        Assert.NotNull(result);
        Assert.Equal(320, result!.Width);
        Assert.Equal(240, result.Height);
        Assert.True(result.FrameCount > 0);
        Assert.True(result.FileSize > 0);
    }

    [Fact]
    public void Probe_InvalidFile_ReturnsNull()
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"mdec_test_{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllBytes(tempFile, new byte[100]);
            var result = StrConverter.Probe(tempFile);
            Assert.Null(result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    // ── Batch Demux Test ───────────────────────────────────────────────

    [CorpusFact]
    public void Demux_AllStrFiles_NoExceptions()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var files = GetAllStrFiles();
        Assert.SkipWhen(files.Length == 0, "No STR files found");

        var errors = new List<string>();
        var demuxed = 0;
        var skippedNonVideo = 0;
        var skippedUnsupported = 0;
        var expectedCorrupt = 0;

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);

                // Some .str files aren't MDEC video (THPS2 level data, DC formats) — skip them
                if (!StrDemuxer.IsStrFile(data))
                {
                    skippedNonVideo++;
                    continue;
                }

                var frameCount = StrDemuxer.CountFrames(data);
                Assert.True(frameCount > 0, $"{Path.GetFileName(file)} has 0 frames");

                // Decode first frame to verify decode pipeline
                var firstFrame = StrDemuxer.EnumerateFrames(data).FirstOrDefault();
                Assert.NotNull(firstFrame);
                Assert.True(firstFrame!.Width > 0 && firstFrame.Width % 16 == 0,
                    $"{Path.GetFileName(file)} has invalid width {firstFrame.Width}");
                Assert.True(firstFrame.Height > 0 && firstFrame.Height % 16 == 0,
                    $"{Path.GetFileName(file)} has invalid height {firstFrame.Height}");

                var version = firstFrame.Data.Length >= 8
                    ? BitConverter.ToUInt16(firstFrame.Data, 6)
                    : 0;
                if (version != 2)
                {
                    skippedUnsupported++;
                    continue;
                }

                var frameSha256 = Convert.ToHexStringLower(SHA256.HashData(firstFrame.Data));
                if (frameSha256 == KnownCorruptSm2E5M6FrameSha256)
                {
                    Assert.Throws<InvalidDataException>(() =>
                        MdecDecoder.DecodeFrame(firstFrame.Data, firstFrame.Width, firstFrame.Height));
                    expectedCorrupt++;
                    continue;
                }

                var rgb = MdecDecoder.DecodeFrame(firstFrame.Data, firstFrame.Width, firstFrame.Height);
                Assert.Equal(firstFrame.Width * firstFrame.Height * 3, rgb.Length);

                demuxed++;
            }
            catch (Exception ex)
            {
                errors.Add($"{Path.GetFileName(file)}: {ex.Message}");
            }
        }

        Assert.True(errors.Count == 0,
            $"Failed to demux+decode {errors.Count}/" +
            $"{files.Length - skippedNonVideo - skippedUnsupported - expectedCorrupt} supported video files:\n" +
            string.Join("\n", errors));
        Assert.True(demuxed > 0, "No files were demuxed");
    }
}
