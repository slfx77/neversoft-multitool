using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Texture.NextGen;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.NextGen;

public sealed class XenImgFileTests(TestPaths paths)
{
    private const string ThawBuild = "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";
    private const string Project8Build = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string ProvingGroundBuild = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Parse_Project8Linear_DecodesEverySupportedDxtFormat(byte format)
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 4, 4, format,
            [SolidBlock(format, 0x07E0)], tiled: false);

        var result = XenImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((4, 4, (uint)format), (texture.Width, texture.Height, texture.Psm));
        Assert.NotNull(texture.Pixels);
        Assert.All(Pixels(texture.Pixels!), pixel => Assert.Equal((byte)0, pixel.R));
        Assert.All(Pixels(texture.Pixels!), pixel => Assert.Equal((byte)255, pixel.G));
        Assert.All(Pixels(texture.Pixels!), pixel => Assert.Equal((byte)0, pixel.B));
        Assert.All(Pixels(texture.Pixels!), pixel => Assert.Equal((byte)255, pixel.A));
    }

    [Theory]
    [InlineData(XenImgFile.ThawMagic, 0, 0, 255)]
    [InlineData(XenImgFile.Project8Magic, 255, 0, 0)]
    public void Parse_UsesVariantSpecificRowOrientation(
        uint magic, byte topR, byte topG, byte topB)
    {
        var data = BuildImage(magic, 4, 8, 1,
        [
            SolidBlock(1, 0xF800), // stored top: red
            SolidBlock(1, 0x001F)  // stored bottom: blue
        ], tiled: false);

        var result = XenImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var pixels = Assert.Single(result.Textures).Pixels!;
        Assert.Equal((topR, topG, topB, (byte)255),
            (pixels[0], pixels[1], pixels[2], pixels[3]));
        var bottom = (8 - 1) * 4 * 4;
        Assert.NotEqual((topR, topG, topB),
            (pixels[bottom], pixels[bottom + 1], pixels[bottom + 2]));
    }

    [Fact]
    public void Parse_TiledDxt5_UntilesCompressedBlocksBeforeDecode()
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 8, 8, 5,
        [
            SolidBlock(5, 0xF800), SolidBlock(5, 0x07E0),
            SolidBlock(5, 0x001F), SolidBlock(5, 0xFFFF)
        ], tiled: true);

        var result = XenImgFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var pixels = Assert.Single(result.Textures).Pixels!;
        AssertPixel(pixels, 8, 0, 0, 255, 0, 0);
        AssertPixel(pixels, 8, 7, 0, 0, 255, 0);
        AssertPixel(pixels, 8, 0, 7, 0, 0, 255);
        AssertPixel(pixels, 8, 7, 7, 255, 255, 255);
    }

    [Fact]
    public void Parse_RawDeflate_DecodesThenValidatesInnerDescriptor()
    {
        var plain = BuildImage(
            XenImgFile.Project8Magic, 4, 4, 1,
            [SolidBlock(1, 0xF800)], tiled: false);
        var wrapped = RawDeflate(plain);

        Assert.NotEqual(XenImgFile.Project8Magic,
            BinaryPrimitives.ReadUInt32BigEndian(wrapped));
        Assert.True(XenImgFile.TryInspect(wrapped, out var info, out var error), error);
        Assert.True(info.WasDeflated);

        var result = XenImgFile.Parse(wrapped);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(4 * 4 * 4, Assert.Single(result.Textures).Pixels!.Length);

        var wrappedJunk = RawDeflate(new byte[4097]);
        Assert.False(XenImgFile.IsXenImg(wrappedJunk));
    }

    [Fact]
    public void Parse_MultiMipDescriptor_ExposesLevelZeroOnly()
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 4, 4, 1,
            [SolidBlock(1, 0xF800)], tiled: false, mipLevels: 7);

        Assert.True(XenImgFile.TryInspect(data, out var info, out var error), error);
        Assert.Equal(7, info.MipLevels);

        var result = XenImgFile.Parse(data);
        Assert.True(result.Success, result.ErrorMessage);
        Assert.Single(result.Textures);
        Assert.Equal(4 * 4 * 4, result.Textures[0].Pixels!.Length);
    }

    [Fact]
    public void Parse_TiledDxn_DecodesBc5ChannelsAndReconstructsPositiveZ()
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 4, 4, 6,
            [SolidDxnBlock(128, 128)], tiled: true);

        Assert.True(XenImgFile.TryInspect(data, out var info, out var error), error);
        Assert.True(info.IsDxn);
        Assert.Equal("DXN/BC5", info.FormatName);

        var result = XenImgFile.Parse(data);
        Assert.True(result.Success, result.ErrorMessage);
        var pixels = Assert.Single(result.Textures).Pixels!;
        Assert.All(Pixels(pixels), pixel =>
            Assert.Equal(((byte)128, (byte)128, (byte)255, (byte)255), pixel));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FormatProbe_RecognizesPlainAndRawDeflateXenImg(bool deflated)
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 4, 4, 1,
            [SolidBlock(1, 0xF800)], tiled: false);
        if (deflated)
            data = RawDeflate(data);

        var file = Path.Combine(Path.GetTempPath(), $"nmt-xen-{Guid.NewGuid():N}.img.xen");
        try
        {
            File.WriteAllBytes(file, data);
            var result = FormatProbe.ProbeTexture(file);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Contains("Xbox 360 IMG", result.FormatName, StringComparison.Ordinal);
            Assert.Contains("DXT1", result.FormatName, StringComparison.Ordinal);
            Assert.Equal(deflated, result.FormatName.Contains("raw-DEFLATE", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FormatProbe_Dxn_ReportsSupportedBc5()
    {
        var data = BuildImage(
            XenImgFile.Project8Magic, 4, 4, 6,
            [new byte[16]], tiled: true);
        var file = Path.Combine(Path.GetTempPath(), $"nmt-xen-{Guid.NewGuid():N}.img.xen");
        try
        {
            File.WriteAllBytes(file, data);
            var result = FormatProbe.ProbeTexture(file);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("Xbox 360 IMG (DXN/BC5)", result.FormatName);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void XbxTexCommand_ImgXen_WritesOnePng()
    {
        var root = Path.Combine(Path.GetTempPath(), $"nmt-xen-cli-{Guid.NewGuid():N}");
        var input = Path.Combine(root, "green.img.xen");
        var output = Path.Combine(root, "out");
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(input, BuildImage(
                XenImgFile.Project8Magic, 4, 4, 5,
                [SolidBlock(5, 0x07E0)], tiled: false));

            Assert.Equal(0, XbxTexCommand.Execute(
                input, output, verbose: false, TestContext.Current.CancellationToken));
            Assert.True(File.Exists(Path.Combine(output, "green.png")));
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(ThawBuild, "data/images/XB360_screens/loadscrn.img.xen", 1280, 720,
        "D203E2C587D61C1EC58C67AAA6823BD933E517BA2BF51B7CD4F31708BF483B27")]
    [InlineData(Project8Build, "DATA/COMPRESSED/IMAGES/LOADINGSCREENS/loadscrn.img.xen", 1280, 720,
        "813777DE8A393A71A5660AE9745D8D63CC766A00AD39E2D1C8752B62E59BF4A4")]
    [InlineData(Project8Build, "DATA/IMAGES/TAGS/tag_hawk.img.xen", 128, 128,
        "10CF40DA30F1AE86A39DC615A3BF7CCA6AF47DD02CF706F81E4AC48B5AC68E17")]
    [InlineData(ProvingGroundBuild,
        "DATA/COMPRESSED/TEX/MODELS/CHARACTERS/SKATER_MALE/BELTS/cas_shared_belt01_n.img.xen",
        64, 64, "35CE5CD4B6C710CC964E525E97343E62FDDAF47D08B997277E8C262DB15620E4")]
    public void Parse_RealFiles_PinLayoutOrientationAndEstablishedBlockOutput(
        string build, string relativePath, int width, int height, string rgbaSha256)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var file = Path.Combine(paths.SampleBuildsDir!, build,
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.SkipWhen(!File.Exists(file), $"Sample not found: {file}");

        var result = XenImgFile.Parse(file);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal((width, height), (texture.Width, texture.Height));
        Assert.Equal(rgbaSha256,
            Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
    }

    [CorpusFact]
    public void Corpus_All13712Files_DecodeLevelZero()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var filesByBuild = new[]
        {
            (ThawBuild, 9203),
            (Project8Build, 3127),
            (ProvingGroundBuild, 1382)
        };

        var files = new List<string>();
        foreach (var (build, expected) in filesByBuild)
        {
            var buildFiles = paths.FindSampleFiles(build, "*.img.xen").ToArray();
            Assert.Equal(expected, buildFiles.Length);
            files.AddRange(buildFiles);
        }

        Assert.Equal(13712, files.Count);

        var deflated = 0;
        var multiMip = 0;
        var dxn = 0;
        var dxt1 = 0;
        var dxt3 = 0;
        var dxt5 = 0;

        foreach (var file in files)
        {
            var data = File.ReadAllBytes(file);
            Assert.True(XenImgFile.TryInspect(data, out var info, out var error),
                $"{file}: {error}");
            if (info.WasDeflated) deflated++;
            if (info.MipLevels > 1) multiMip++;

            var result = XenImgFile.Parse(data);
            Assert.True(result.Success, $"{file}: {result.ErrorMessage}");
            var texture = Assert.Single(result.Textures);
            Assert.NotNull(texture.Pixels);
            Assert.Equal(checked(info.Width * info.Height * 4), texture.Pixels!.Length);
            Assert.Equal((info.Width, info.Height), (texture.Width, texture.Height));

            switch (info.DescriptorFormat)
            {
                case 1: dxt1++; break;
                case 2: dxt3++; break;
                case 5: dxt5++; break;
                case 6:
                    Assert.True(info.IsDxn, file);
                    dxn++;
                    break;
                default: Assert.Fail($"{file}: unexpected descriptor format {info.DescriptorFormat}"); break;
            }
        }

        Assert.Equal(1034, deflated);
        Assert.Equal(601, multiMip);
        Assert.Equal(14, dxn);
        Assert.Equal(3312, dxt1);
        Assert.Equal(298, dxt3);
        Assert.Equal(10088, dxt5);
        Assert.Equal(13712, dxt1 + dxt3 + dxt5 + dxn);
    }

    private static byte[] BuildImage(
        uint magic,
        int width,
        int height,
        byte format,
        IReadOnlyList<byte[]> blocks,
        bool tiled,
        byte mipLevels = 1)
    {
        var blockBytes = format == 1 ? 8 : 16;
        var blocksWide = (width + 3) / 4;
        var blocksHigh = (height + 3) / 4;
        Assert.Equal(blocksWide * blocksHigh, blocks.Count);
        Assert.All(blocks, block => Assert.Equal(blockBytes, block.Length));

        byte[] payload;
        if (tiled)
        {
            var offsets = new int[blocks.Count];
            var maximum = 0;
            for (var y = 0; y < blocksHigh; y++)
            {
                for (var x = 0; x < blocksWide; x++)
                {
                    var index = y * blocksWide + x;
                    offsets[index] = TiledOffset(x, y, Align(blocksWide, 32), blockBytes);
                    maximum = Math.Max(maximum, offsets[index]);
                }
            }

            payload = new byte[(maximum + 1) * blockBytes];
            for (var i = 0; i < blocks.Count; i++)
                blocks[i].CopyTo(payload, offsets[i] * blockBytes);
        }
        else
        {
            payload = new byte[blocks.Count * blockBytes];
            for (var i = 0; i < blocks.Count; i++)
                blocks[i].CopyTo(payload, i * blockBytes);
        }

        Swap16(payload);

        var data = new byte[XenImgFile.LevelZeroOffset + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, magic);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(8), (ushort)width);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(10), (ushort)height);

        var fetchFormat = format switch
        {
            1 => 0x12,
            2 => 0x13,
            5 => 0x14,
            6 => 0x31,
            _ => throw new ArgumentOutOfRangeException(nameof(format))
        };
        var depth = format is 1 or 2 ? (byte)4 : (byte)8;
        var fetchOffset = magic == XenImgFile.ThawMagic ? 0x30 : 0x44;
        if (magic == XenImgFile.ThawMagic)
        {
            data[0x10] = mipLevels;
            data[0x11] = depth;
            data[0x13] = format;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x18), (uint)payload.Length);
        }
        else
        {
            data[0x14] = mipLevels;
            data[0x15] = depth;
            data[0x16] = format;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0x20), (uint)payload.Length);
        }

        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(fetchOffset), tiled ? 0x80000000u : 0u);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(fetchOffset + 4), (uint)(0x40 | fetchFormat));
        payload.CopyTo(data, XenImgFile.LevelZeroOffset);
        return data;
    }

    private static byte[] SolidBlock(byte format, ushort rgb565)
    {
        var block = new byte[format == 1 ? 8 : 16];
        var colorOffset = format == 1 ? 0 : 8;
        if (format == 2)
            block.AsSpan(0, 8).Fill(0xFF);
        else if (format == 5)
            block[0] = 0xFF;

        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(colorOffset), rgb565);
        BinaryPrimitives.WriteUInt16LittleEndian(block.AsSpan(colorOffset + 2), 0);
        return block;
    }

    private static byte[] SolidDxnBlock(byte red, byte green)
    {
        var block = new byte[16];
        block[0] = red;
        block[1] = red;
        block[8] = green;
        block[9] = green;
        return block;
    }

    private static byte[] RawDeflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            deflate.Write(data);
        return output.ToArray();
    }

    private static IEnumerable<(byte R, byte G, byte B, byte A)> Pixels(byte[] rgba)
    {
        for (var i = 0; i < rgba.Length; i += 4)
            yield return (rgba[i], rgba[i + 1], rgba[i + 2], rgba[i + 3]);
    }

    private static void AssertPixel(
        byte[] rgba, int width, int x, int y, byte r, byte g, byte b)
    {
        var offset = (y * width + x) * 4;
        Assert.Equal((r, g, b, (byte)255),
            (rgba[offset], rgba[offset + 1], rgba[offset + 2], rgba[offset + 3]));
    }

    private static void Swap16(Span<byte> bytes)
    {
        for (var i = 0; i + 1 < bytes.Length; i += 2)
            (bytes[i], bytes[i + 1]) = (bytes[i + 1], bytes[i]);
    }

    // Independent test-side encoder for the documented XGAddress mapping.
    private static int TiledOffset(int x, int y, int width, int bytesPerBlock)
    {
        var aw = Align(width, 32);
        var log = (bytesPerBlock >> 2) + ((bytesPerBlock >> 1) >> (bytesPerBlock >> 2));
        var macro = ((x >> 5) + (y >> 5) * (aw >> 5)) << (log + 7);
        var micro = ((x & 7) + ((y & 6) << 2)) << log;
        var offset = macro + (micro & ~15) * 2 + (micro & 15)
                     + ((y & 8) << (3 + log)) + ((y & 1) << 4);
        return ((offset & ~511) * 8 + (offset & 448) * 4 + (offset & 63)
                + ((y & 16) << 7) + ((((y & 8) >> 2) + (x >> 3)) & 3) * 64) >> log;
    }

    private static int Align(int value, int alignment)
    {
        return (value + alignment - 1) & ~(alignment - 1);
    }
}
