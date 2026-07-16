using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

public class ArchiveFileSystemTests(TestPaths paths)
{
    // --- synthetic fixture builders -------------------------------------

    /// <summary>PRE v3 (0xABCD0003) buffer with uncompressed entries.</summary>
    private static byte[] BuildCompressedPreV3(params (string Name, byte[] Data)[] files)
    {
        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(0); // totalFileSize placeholder
        writer.Write(0xABCD0003u);
        writer.Write(files.Length);

        foreach (var (name, data) in files)
        {
            var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
            writer.Write(data.Length); // dataSize
            writer.Write(0); // compressedDataSize = 0 → stored uncompressed
            writer.Write((short)nameBytes.Length);
            writer.Write((short)0);
            writer.Write(0u); // checksum
            writer.Write(nameBytes);
            writer.Write(data);
            var pad = (4 - data.Length % 4) % 4;
            writer.Write(new byte[pad]);
        }

        var buffer = ms.ToArray();
        BitConverter.GetBytes(buffer.Length).CopyTo(buffer, 0);
        return buffer;
    }

    /// <summary>PS1 plaintext WAD+HED pair written to a temp dir.</summary>
    private static (string WadPath, string TempDir) BuildWadOnDisk(params (string Name, byte[] Data)[] files)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_Fs_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        using var wad = new MemoryStream();
        using var hed = new MemoryStream();
        using var hedWriter = new BinaryWriter(hed);

        foreach (var (name, data) in files)
        {
            var offset = (uint)wad.Length;
            wad.Write(data);

            var nameBytes = Encoding.ASCII.GetBytes(name + "\0");
            hedWriter.Write(nameBytes);
            var pad = (4 - hed.Length % 4) % 4;
            hedWriter.Write(new byte[pad]);
            hedWriter.Write(offset);
            hedWriter.Write((uint)data.Length);
        }

        hedWriter.Write((byte)0xFF);

        var wadPath = Path.Combine(tempDir, "TEST.WAD");
        File.WriteAllBytes(wadPath, wad.ToArray());
        File.WriteAllBytes(Path.Combine(tempDir, "TEST.HED"), hed.ToArray());
        return (wadPath, tempDir);
    }

    // --- tests -----------------------------------------------------------

    [Fact]
    public void FindByPath_NormalizesSeparatorsAndCase()
    {
        var pre = BuildCompressedPreV3(("levels\\ap\\ap.bsp", [1, 2, 3, 4]));
        using var fs = ArchiveFileSystem.TryOpen(pre, "test.pre", "test.pre");

        Assert.NotNull(fs);
        Assert.Equal(ArchiveAssetType.CompressedPre, fs!.Type);
        Assert.NotNull(fs.FindByPath("levels/ap/ap.bsp"));
        Assert.NotNull(fs.FindByPath("LEVELS\\AP\\AP.BSP"));
        Assert.NotNull(fs.FindByName("ap.bsp"));
        Assert.Null(fs.FindByPath("levels/ap/missing.bsp"));
    }

    [Fact]
    public void FindAllByName_ReturnsEveryDirectoryVariant()
    {
        var pre = BuildCompressedPreV3(
            ("a\\tex.img", [1]),
            ("b\\tex.img", [2]),
            ("c\\other.img", [3]));
        using var fs = ArchiveFileSystem.TryOpen(pre, "test.pre", "test.pre");

        Assert.NotNull(fs);
        Assert.Equal(2, fs!.FindAllByName("tex.img").Count);
        Assert.Single(fs.FindAllByName("other.img"));
        Assert.Empty(fs.FindAllByName("absent.img"));

        // First-wins flat lookup matches the historical backend behavior.
        var first = fs.FindByName("tex.img");
        Assert.NotNull(first);
        Assert.Equal("a", first!.Directory);
    }

    [Fact]
    public void ReadEntry_RoundTripsStoredBytes()
    {
        var payload = Enumerable.Range(0, 251).Select(i => (byte)i).ToArray();
        var pre = BuildCompressedPreV3(("data.bin", payload));
        using var fs = ArchiveFileSystem.TryOpen(pre, "test.pre", "test.pre");

        var entry = fs!.FindByName("data.bin");
        Assert.NotNull(entry);
        Assert.Equal(payload, fs.ReadEntry(entry!));
    }

    [Fact]
    public void TryOpenNested_PreInsideWad_ListsAndReadsInnerEntries()
    {
        var innerPayload = Encoding.ASCII.GetBytes("BSP-DATA-0123456789");
        var pre = BuildCompressedPreV3(("Levels\\Ap\\Ap.bsp", innerPayload));
        var (wadPath, tempDir) = BuildWadOnDisk(
            ("filler.bin", [9, 9, 9, 9]),
            ("pre\\ApBSP.pre", pre));

        try
        {
            using var fs = ArchiveFileSystem.TryOpen(wadPath);
            Assert.NotNull(fs);
            Assert.Equal(ArchiveAssetType.Wad, fs!.Type);

            var preEntry = fs.FindByName("pre\\ApBSP.pre") ?? fs.Entries.First(e => e.Name.EndsWith(".pre"));
            using var nested = fs.TryOpenNested(preEntry);

            Assert.NotNull(nested);
            Assert.Equal(ArchiveAssetType.CompressedPre, nested!.Type);
            Assert.Equal(1, nested.NestingDepth);
            Assert.Contains("::", nested.DisplayPath);
            Assert.Equal(wadPath, nested.ContainerPath);

            var inner = nested.FindByName("Ap.bsp");
            Assert.NotNull(inner);
            Assert.Equal(innerPayload, nested.ReadEntry(inner!));

            // Non-archive entries refuse to nest.
            var filler = fs.FindByName("filler.bin");
            Assert.Null(fs.TryOpenNested(filler!));
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ReadEntry_ConcurrentReadsMatchSequential()
    {
        Assert.SkipWhen(paths.PkrDir == null || !Directory.Exists(paths.PkrDir), "PKR fixture not available");
        var pkrPath = Path.Combine(paths.PkrDir!, "test.pkr");
        Assert.SkipWhen(!File.Exists(pkrPath), "test.pkr not available");

        using var fs = ArchiveFileSystem.TryOpen(pkrPath);
        Assert.NotNull(fs);

        var expected = fs!.Entries.ToDictionary(e => e.FullName, fs.ReadEntry);

        Parallel.For(0, 8, _ =>
        {
            foreach (var entry in fs.Entries)
                Assert.Equal(expected[entry.FullName], fs.ReadEntry(entry));
        });
    }

    [Fact]
    public void ReadEntry_OversizedEntry_ThrowsInvalidData()
    {
        var pre = BuildCompressedPreV3(("data.bin", [1, 2, 3, 4]));
        using var fs = ArchiveFileSystem.TryOpen(pre, "test.pre", "test.pre");

        var bogus = new ArchiveEntry { Name = "huge.bin", Size = 3L << 30, Offset = 0 };
        Assert.Throws<InvalidDataException>(() => fs!.ReadEntry(bogus));

        var outOfRange = new ArchiveEntry { Name = "oob.bin", Size = 64, Offset = pre.Length };
        Assert.Throws<InvalidDataException>(() => fs!.ReadEntry(outOfRange));
    }

    [Fact]
    public void Decoder_DoesNotDecompressOverloadedFlags()
    {
        // PAK repurposes IsCompressed as "has .pab companion" and THUG WAD as
        // NO_WAD — the decoder must pass both through as raw copies.
        var raw = new byte[] { 0x10, 0x20, 0x30, 0x40 };
        var pakEntry = new ArchiveEntry { Name = "x.skin", Size = 4, IsCompressed = true };
        Assert.Same(raw, ArchiveEntryDecoder.Decode(ArchiveAssetType.Pak, pakEntry, raw));
        Assert.Equal(4, ArchiveEntryDecoder.GetStoredSize(ArchiveAssetType.Pak, pakEntry));

        var wadEntry = new ArchiveEntry { Name = "y.bin", Size = 4, IsCompressed = true };
        Assert.Same(raw, ArchiveEntryDecoder.Decode(ArchiveAssetType.Wad, wadEntry, raw));
    }

    [Fact]
    public void Wad_NoWadEntry_ThrowsDescriptiveError()
    {
        var (wadPath, tempDir) = BuildWadOnDisk(("real.bin", [1, 2, 3, 4]));
        try
        {
            using var fs = ArchiveFileSystem.TryOpen(wadPath);
            var noWad = new ArchiveEntry
            {
                Name = "external.bin", Directory = "gfx", Size = 4, Offset = 0, IsCompressed = true
            };

            var ex = Assert.Throws<InvalidDataException>(() => fs!.ReadEntry(noWad));
            Assert.Contains("NO_WAD", ex.Message);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BackendShim_PreservesLegacyBehavior()
    {
        Assert.SkipWhen(paths.WadDir == null || !Directory.Exists(paths.WadDir), "WAD fixture not available");
        var wadPath = Path.Combine(paths.WadDir!, "CD.WAD");
        Assert.SkipWhen(!File.Exists(wadPath), "CD.WAD not available");

        var backend = ArchiveAssetBackend.TryOpen(wadPath);
        Assert.NotNull(backend);
        Assert.Equal(wadPath, backend!.ArchivePath);
        Assert.Equal(ArchiveAssetType.Wad, backend.Type);
        Assert.NotEmpty(backend.Entries);

        var legacy = WadArchive.GetFileList(wadPath);
        Assert.Equal(legacy.Count, backend.Entries.Count);

        var first = backend.Entries.First(e => e.Size > 0);
        var bytes = backend.ReadEntryBytes(first);
        Assert.Equal(first.Size, bytes.Length);
        Assert.NotNull(backend.FindEntry(first.Name));
    }
}
