using System.Security.Cryptography;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.Ps2Scene.ZoneTex;

/// <summary>
///     Exact THAW PS2 texture regressions read through DATAP.WAD and nested PAKs.
///     The cases intentionally avoid extracted archive trees because older PAK
///     extraction code wrote shifted payloads.
/// </summary>
public sealed class ThawArchiveTextureRegressionTests(TestPaths paths)
{
    private const string ThawPs2Build = "Tony Hawk's American Wasteland (2005-8-22, PS2 - Final)";

    private string WadPath => paths.SampleBuildsDir is null
        ? string.Empty
        : Path.Combine(paths.SampleBuildsDir, ThawPs2Build, "DATAP.WAD");

    [Fact]
    public void ByteZeroOwnerBlobHeader_IsRecognizedWithoutSampleData()
    {
        // One 0x50-byte primary plus one 0x40-byte secondary puts the DMA tag
        // at 0xA0. This is the exact owner-blob shape used by standalone .stex.
        var data = new byte[0xC0];
        BitConverter.GetBytes((ushort)6).CopyTo(data, 0x00);
        BitConverter.GetBytes((ushort)1).CopyTo(data, 0x02);
        BitConverter.GetBytes(1).CopyTo(data, 0x04);
        BitConverter.GetBytes(0xA0).CopyTo(data, 0x08);
        BitConverter.GetBytes(0xA0).CopyTo(data, 0x0C);
        BitConverter.GetBytes(0x10000006u).CopyTo(data, 0xA0);
        BitConverter.GetBytes(1UL).CopyTo(data, 0xB0);
        BitConverter.GetBytes(0x0EUL).CopyTo(data, 0xB8);

        Assert.True(ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
            data, out var headerOffset, out var primaryCount, out var secondaryCount,
            out var baseA, out var baseB, out var dmaStart));
        Assert.Equal(0, headerOffset);
        Assert.Equal(1, primaryCount);
        Assert.Equal(1, secondaryCount);
        Assert.Equal(0xA0, baseA);
        Assert.Equal(0xA0, baseB);
        Assert.Equal(0xA0, dmaStart);
    }

    [CorpusFact]
    public void ReportedStandaloneStexOwnerBlobs_DecodeFromByteZeroHeader()
    {
        Assert.SkipWhen(!File.Exists(WadPath), "THAW PS2 DATAP.WAD sample not available");

        using var wad = ArchiveFileSystem.TryOpen(WadPath);
        Assert.SkipWhen(wad == null, "DATAP.WAD did not open as a WAD archive");

        var cases = new[]
        {
            new ReportedStexCase(
                "worlds/worldzones/z_sz/z_szped.pak.ps2",
                "0003B210.stex",
                0x1B8E913Eu,
                64,
                128,
                "7338E47A60A349112A60A1FAAF9F18ACF922E9F919AE7AFD7BF47518F5FB7949"),
            new ReportedStexCase(
                "cutscenes/bh_levelevent/ps2/bh_levelevent_main/bh_levelevent_main.pak.ps2",
                "000E5E10.stex",
                0x09348027u,
                128,
                32,
                "CE804539B09781004EBB9B89952E4CB7B76E964F630C172F7138F99ADC680EE2")
        };

        foreach (var regression in cases)
        {
            var data = ReadNestedEntry(wad!, regression.PakPath, regression.EntryName);
            var entries = ThawZoneTexFile.ParseHeaderEntries(data);

            Assert.Single(entries);
            Assert.Equal(regression.Checksum, entries[0].Checksum);
            Assert.True(ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
                data, out var headerOffset, out var primaryCount, out var secondaryCount,
                out _, out _, out var dmaStart));
            Assert.Equal(0, headerOffset);
            Assert.Equal(1, primaryCount);
            Assert.Equal(1, secondaryCount);
            Assert.Equal(0xA0, dmaStart);

            var texture = Assert.Single(ThawZoneTexFile.DecodeAllFromFile(data));
            Assert.Equal(regression.Checksum, texture.Checksum);
            Assert.Equal(regression.Width, texture.Width);
            Assert.Equal(regression.Height, texture.Height);
            Assert.NotNull(texture.Pixels);
            Assert.Equal(regression.RgbaSha256,
                Convert.ToHexString(SHA256.HashData(texture.Pixels!)));
        }
    }

    [CorpusFact]
    public void CagrAssetImg_PreservesItsStoredAlphaChannel()
    {
        Assert.SkipWhen(!File.Exists(WadPath), "THAW PS2 DATAP.WAD sample not available");

        using var wad = ArchiveFileSystem.TryOpen(WadPath);
        Assert.SkipWhen(wad == null, "DATAP.WAD did not open as a WAD archive");

        var data = ReadNestedEntry(
            wad!,
            "pak/cagr_assets/cagr_assets_g.pak.ps2",
            "00005B30.img");
        var result = Ps2TexFile.Parse(data);

        Assert.True(result.Success, result.ErrorMessage);
        var texture = Assert.Single(result.Textures);
        Assert.Equal(0xB57E6838u, texture.Checksum);
        Assert.Equal(32, texture.Width);
        Assert.Equal(32, texture.Height);
        Assert.NotNull(texture.Pixels);

        var alphaValues = texture.Pixels!
            .Where((_, index) => index % 4 == 3)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(9, alphaValues.Length);
        Assert.Equal(0, alphaValues[0]);
        Assert.Equal(255, alphaValues[^1]);

        var png = ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Pixels!);
        using var emitted = Image.Load<Rgba32>(png);
        var emittedAlphaValues = Enumerable.Range(0, emitted.Width * emitted.Height)
            .Select(index => emitted[index % emitted.Width, index / emitted.Width].A)
            .Distinct()
            .Order()
            .ToArray();
        Assert.Equal(alphaValues, emittedAlphaValues);
    }

    private static byte[] ReadNestedEntry(
        IArchiveFileSystem wad,
        string pakPath,
        string entryName)
    {
        var pakEntry = wad.FindByPath(pakPath);
        Assert.NotNull(pakEntry);

        using var pak = wad.TryOpenNested(pakEntry!);
        Assert.NotNull(pak);

        var entry = pak!.FindByName(entryName);
        Assert.NotNull(entry);
        return pak.ReadEntry(entry!);
    }

    private sealed record ReportedStexCase(
        string PakPath,
        string EntryName,
        uint Checksum,
        int Width,
        int Height,
        string RgbaSha256);
}
