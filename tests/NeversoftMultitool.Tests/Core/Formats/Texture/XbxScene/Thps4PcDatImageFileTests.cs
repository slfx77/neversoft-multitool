using System.Buffers.Binary;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.XbxScene;

public sealed class Thps4PcDatImageFileTests(TestPaths paths)
{
    private const string PcBuildName = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";
    private const string Ps2BuildName = "Tony Hawk's Pro Skater 4 (2002-9-30, PS2 - Final)";
    private const int HeaderSize = 32;

    [Theory]
    [InlineData("blackimg.dat", true)]
    [InlineData("AspyrScreenIMG.DAT", true)]
    [InlineData(@"images\PanelSprites\whiteimg.dat", true)]
    [InlineData("black.img.dat", false)]
    [InlineData("img.dat", false)]
    [InlineData("blackimg.bin", false)]
    [InlineData("blackimg.dat.bak", false)]
    public void NameGate_AdmitsOnlyDelimiterFreeImgDatNames(string name, bool expected)
    {
        Assert.Equal(expected, Thps4PcDatImageFile.IsCandidateFileName(name));
    }

    [Fact]
    public void Parse_P8_UsesBottomAlignedBottomUpMortonSurface()
    {
        var result = Thps4PcDatImageFile.Parse(BuildP8Image());

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(0x00410230u, texture.Checksum);
        Assert.Equal(3, texture.Width);
        Assert.Equal(3, texture.Height);
        Assert.Equal(
            ExpectedPalettePixels([10, 11, 14, 8, 9, 12, 2, 3, 6]),
            texture.Pixels);
    }

    [Fact]
    public void Parse_Bgra32_FlipsRowsAndChannels()
    {
        var result = Thps4PcDatImageFile.Parse(BuildBgra32Image());

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(3, texture.Width);
        Assert.Equal(2, texture.Height);
        Assert.Equal(
            ExpectedRawPixels([3, 4, 5, 0, 1, 2]),
            texture.Pixels);
    }

    [Fact]
    public void Parse_RequiresTheChecksumAndExactPayloadBounds()
    {
        var valid = BuildP8Image();

        var wrongChecksum = valid.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongChecksum.AsSpan(4), 0);
        var checksumResult = Thps4PcDatImageFile.Parse(wrongChecksum);
        Assert.False(checksumResult.Success);
        Assert.Contains("checksum", checksumResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var truncated = Thps4PcDatImageFile.Parse(valid.AsSpan(0, valid.Length - 1));
        Assert.False(truncated.Success);
        Assert.Contains("truncated", truncated.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var trailing = Thps4PcDatImageFile.Parse([.. valid, 0]);
        Assert.False(trailing.Success);
        Assert.Contains("trailing", trailing.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_ValidatesPaddedIndicesAndCanonicalDimensions()
    {
        var badIndex = BuildP8Image();
        badIndex[^1] = 16;
        var indexResult = Thps4PcDatImageFile.Parse(badIndex);
        Assert.False(indexResult.Success);
        Assert.Contains("palette index 16", indexResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);

        var nonCanonicalDimensions = BuildP8Image();
        BinaryPrimitives.WriteUInt16LittleEndian(nonCanonicalDimensions.AsSpan(24), 2);
        var dimensionResult = Thps4PcDatImageFile.Parse(nonCanonicalDimensions);
        Assert.False(dimensionResult.Success);
        Assert.Contains("do not match", dimensionResult.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProbeAndCli_RouteAndExportWithTheCleanStem()
    {
        var directory = CreateTempDirectory();
        var validPath = Path.Combine(directory, "badgeimg.dat");
        var malformedPath = Path.Combine(directory, "brokenimg.dat");
        var output = Path.Combine(directory, "output");
        var malformedOutput = Path.Combine(directory, "malformed-output");
        var scanDirectory = Path.Combine(directory, "scan");
        var scanOutput = Path.Combine(directory, "scan-output");
        var data = BuildP8Image();
        File.WriteAllBytes(validPath, data);
        File.WriteAllBytes(malformedPath, [.. data, 0]);
        Directory.CreateDirectory(scanDirectory);
        File.WriteAllBytes(Path.Combine(scanDirectory, "scanbadgeimg.dat"), data);

        try
        {
            var probe = FormatProbe.ProbeTexture(validPath);
            Assert.Equal(FormatProbe.FormatSupport.Supported, probe.Support);
            Assert.Equal("THPS4 PC IMG (3x3)", probe.FormatName);

            var rejected = FormatProbe.ProbeTexture(malformedPath);
            Assert.Equal(FormatProbe.FormatSupport.Unsupported, rejected.Support);
            Assert.Contains("trailing", rejected.UnsupportedReason, StringComparison.OrdinalIgnoreCase);

            Assert.Equal(0, XbxTexCommand.Execute(
                validPath,
                output,
                verbose: false,
                TestContext.Current.CancellationToken));
            var pngPath = Path.Combine(output, "badge.png");
            Assert.True(File.Exists(pngPath));
            var png = File.ReadAllBytes(pngPath);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)));
            Assert.Equal(3u, BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)));

            Assert.Equal(0, XbxTexCommand.Execute(
                scanDirectory,
                scanOutput,
                verbose: false,
                TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(scanOutput, "scanbadge.png")));

            Assert.Equal(1, XbxTexCommand.Execute(
                malformedPath,
                malformedOutput,
                verbose: false,
                TestContext.Current.CancellationToken));
            Assert.False(File.Exists(Path.Combine(malformedOutput, "broken.png")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [CorpusFact]
    public void Corpus_All880FilesParseProbeAndDecodeExactly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var files = paths.FindSampleFiles(PcBuildName, "*img.dat")
            .Where(file => Thps4PcDatImageFile.IsCandidateFileName(Path.GetFileName(file)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(880, files.Length);

        long totalBytes = 0;
        long logicalPixels = 0;
        long decodedBytes = 0;
        var p8Count = 0;
        var rawCount = 0;
        var palette16Count = 0;
        var palette256Count = 0;
        var paddedP8Count = 0;
        var failures = new List<string>();
        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            totalBytes += data.LongLength;

            var probe = FormatProbe.ProbeTexture(file);
            if (probe.Support != FormatProbe.FormatSupport.Supported)
            {
                failures.Add($"{Path.GetFileName(file)} probe: {probe.UnsupportedReason}");
                continue;
            }

            var result = Thps4PcDatImageFile.Parse(data);
            if (!result.Success || result.Textures.Count != 1)
            {
                failures.Add($"{Path.GetFileName(file)} parse: {result.ErrorMessage}");
                continue;
            }

            var texture = result.Textures[0];
            if (texture.Pixels == null
                || texture.Pixels.LongLength != (long)texture.Width * texture.Height * 4)
            {
                failures.Add($"{Path.GetFileName(file)}: invalid RGBA output");
                continue;
            }

            logicalPixels += (long)texture.Width * texture.Height;
            decodedBytes += texture.Pixels.LongLength;
            var format = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(16));
            if (format == 0x13)
            {
                p8Count++;
                var paletteSize = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(28));
                if (paletteSize == 64)
                    palette16Count++;
                else if (paletteSize == 1024)
                    palette256Count++;

                var widthExponent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
                var heightExponent = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(12));
                if (texture.Width != 1 << (int)widthExponent
                    || texture.Height != 1 << (int)heightExponent)
                {
                    paddedP8Count++;
                }
            }
            else if (format == 0)
            {
                rawCount++;
            }
        }

        Assert.Empty(failures);
        Assert.Equal(34_553_888, totalBytes);
        Assert.Equal(12_133_382, logicalPixels);
        Assert.Equal(48_533_528, decodedBytes);
        Assert.Equal(841, p8Count);
        Assert.Equal(39, rawCount);
        Assert.Equal(349, palette16Count);
        Assert.Equal(492, palette256Count);
        Assert.Equal(103, paddedP8Count);
    }

    [CorpusFact]
    public void Corpus_Ps2TwinsProveIndexedCropAndRawRowOrientation()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var pcComp = Assert.Single(paths.FindSampleFiles(PcBuildName, "comp_base_lineimg.dat"));
        var pcLoadscreen = Assert.Single(paths.FindSampleFiles(PcBuildName, "loadscrnimg.dat"));
        var ps2Comp = FindMainPs2File("images", "PanelSprites", "comp_base_line.img.ps2");
        var ps2Loadscreen = FindMainPs2File("images", "loadscrn.img.ps2");

        var pcCompTexture = ParseOne(Thps4PcDatImageFile.Parse(pcComp));
        var ps2CompTexture = ParseOne(Ps2TexFile.Parse(ps2Comp));
        Assert.Equal((ps2CompTexture.Width, ps2CompTexture.Height),
            (pcCompTexture.Width, pcCompTexture.Height));
        Assert.Equal(90, CountExactRgbChannels(pcCompTexture.Pixels!, ps2CompTexture.Pixels!));
        AssertAllRgbChannelsWithin(pcCompTexture.Pixels!, ps2CompTexture.Pixels!, 0);

        var pcLoadscreenTexture = ParseOne(Thps4PcDatImageFile.Parse(pcLoadscreen));
        var ps2LoadscreenTexture = ParseOne(Ps2TexFile.Parse(ps2Loadscreen));
        Assert.Equal((ps2LoadscreenTexture.Width, ps2LoadscreenTexture.Height),
            (pcLoadscreenTexture.Width, pcLoadscreenTexture.Height));
        Assert.Equal(
            274_527,
            CountExactRgbChannels(pcLoadscreenTexture.Pixels!, ps2LoadscreenTexture.Pixels!));
        // The PS2 twin uses 5-bit channels; every PC BGRA8 channel lands within
        // one quantization step after the PC's bottom-up rows are corrected.
        AssertAllRgbChannelsWithin(pcLoadscreenTexture.Pixels!, ps2LoadscreenTexture.Pixels!, 8);
    }

    private string FindMainPs2File(params string[] relativeParts)
    {
        var suffix = Path.Combine(["SKATE4", .. relativeParts]);
        return Assert.Single(
            paths.FindSampleFiles(Ps2BuildName, Path.GetFileName(suffix)),
            file => file.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static Ps2Texture ParseOne(Ps2TexResult result)
    {
        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.NotNull(texture.Pixels);
        return texture;
    }

    private static int CountExactRgbChannels(byte[] left, byte[] right)
    {
        Assert.Equal(left.Length, right.Length);
        var count = 0;
        for (var offset = 0; offset < left.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                if (left[offset + channel] == right[offset + channel])
                    count++;
            }
        }

        return count;
    }

    private static void AssertAllRgbChannelsWithin(byte[] left, byte[] right, int maximumDelta)
    {
        Assert.Equal(left.Length, right.Length);
        var measuredMaximum = 0;
        for (var offset = 0; offset < left.Length; offset += 4)
        {
            for (var channel = 0; channel < 3; channel++)
            {
                measuredMaximum = Math.Max(
                    measuredMaximum,
                    Math.Abs(left[offset + channel] - right[offset + channel]));
            }
        }

        Assert.InRange(measuredMaximum, 0, maximumDelta);
    }

    private static byte[] BuildP8Image()
    {
        const int widthExponent = 2;
        const int heightExponent = 2;
        const int paletteEntries = 16;
        const int surfacePixels = (1 << widthExponent) * (1 << heightExponent);
        var data = new byte[HeaderSize + paletteEntries * 4 + surfacePixels];
        WriteHeader(data, widthExponent, heightExponent, 0x13, 3, 3, paletteEntries * 4);

        for (var index = 0; index < paletteEntries; index++)
        {
            var offset = HeaderSize + index * 4;
            data[offset] = (byte)index;
            data[offset + 1] = (byte)(0x20 + index);
            data[offset + 2] = (byte)(0x40 + index);
            data[offset + 3] = (byte)(0xFF - index);
        }

        for (var index = 0; index < surfacePixels; index++)
            data[HeaderSize + paletteEntries * 4 + index] = (byte)index;

        return data;
    }

    private static byte[] BuildBgra32Image()
    {
        const int width = 3;
        const int height = 2;
        var data = new byte[HeaderSize + width * height * 4];
        WriteHeader(data, 1, 1, 0, width, height, 0);
        for (var index = 0; index < width * height; index++)
        {
            var offset = HeaderSize + index * 4;
            data[offset] = (byte)index;
            data[offset + 1] = (byte)(0x20 + index);
            data[offset + 2] = (byte)(0x40 + index);
            data[offset + 3] = (byte)(0xFF - index);
        }

        return data;
    }

    private static void WriteHeader(
        Span<byte> data,
        int widthExponent,
        int heightExponent,
        uint format,
        int width,
        int height,
        int paletteSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(data, 2);
        BinaryPrimitives.WriteUInt32LittleEndian(data[4..], 0x00410230);
        BinaryPrimitives.WriteUInt32LittleEndian(data[8..], (uint)widthExponent);
        BinaryPrimitives.WriteUInt32LittleEndian(data[12..], (uint)heightExponent);
        BinaryPrimitives.WriteUInt32LittleEndian(data[16..], format);
        BinaryPrimitives.WriteUInt16LittleEndian(data[24..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data[26..], (ushort)height);
        BinaryPrimitives.WriteUInt32LittleEndian(data[28..], (uint)paletteSize);
    }

    private static byte[] ExpectedPalettePixels(int[] indices)
    {
        return indices.SelectMany(index => new byte[]
        {
            (byte)(0x40 + index),
            (byte)(0x20 + index),
            (byte)index,
            (byte)(0xFF - index)
        }).ToArray();
    }

    private static byte[] ExpectedRawPixels(int[] indices)
    {
        return ExpectedPalettePixels(indices);
    }

    private static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"nmt-thps4-pc-img-dat-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }
}
