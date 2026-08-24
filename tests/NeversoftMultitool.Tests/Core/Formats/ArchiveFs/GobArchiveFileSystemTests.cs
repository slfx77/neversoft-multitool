using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

/// <summary>
///     Pins the GOB container's wiring: detection gates on the companion index,
///     the disk-backed filesystem reads chunk chains, and a cart's <c>main.gob</c>
///     opens in place through <see cref="IArchiveFileSystem.TryOpenNested" /> with
///     the sibling <c>main.gfc</c> resolved as its companion.
/// </summary>
public sealed class GobArchiveFileSystemTests(TestPaths paths)
{
    private static readonly byte[] Payload =
        Encoding.ASCII.GetBytes("gob entry payload " + new string('x', 200));

    [Fact]
    public void DetectAndOpen_RequiresTheCompanionIndex()
    {
        var (gfc, gob) = BuildPair();
        var dir = Directory.CreateTempSubdirectory("nmt-gob-").FullName;
        try
        {
            var gobPath = Path.Combine(dir, "main.gob");
            var gfcPath = Path.Combine(dir, "main.gfc");
            File.WriteAllBytes(gobPath, gob);

            // No index yet: the blob alone is not an archive.
            Assert.False(GobArchive.IsGobArchive(gobPath));
            Assert.Null(ArchiveTypeDetector.DetectAssetType(gobPath));
            Assert.Equal("GOB (raw)", ArchiveTypeDetector.Classify(gobPath));
            Assert.Null(ArchiveFileSystem.TryOpen(gobPath));

            File.WriteAllBytes(gfcPath, gfc);
            Assert.True(GobArchive.IsGobArchive(gobPath));
            Assert.Equal(ArchiveAssetType.Gob, ArchiveTypeDetector.DetectAssetType(gobPath));
            Assert.Equal("GOB", ArchiveTypeDetector.Classify(gobPath));

            using var fs = ArchiveFileSystem.TryOpen(gobPath);
            Assert.NotNull(fs);
            Assert.Equal(ArchiveAssetType.Gob, fs!.Type);
            var entry = Assert.Single(fs.Entries);
            Assert.Equal(Payload.Length, entry.Size);
            Assert.Equal(Payload, fs.ReadEntry(entry));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Detection_RejectsAnIndexDescribingADifferentBlob()
    {
        var (gfc, gob) = BuildPair();
        var dir = Directory.CreateTempSubdirectory("nmt-gob-").FullName;
        try
        {
            var gobPath = Path.Combine(dir, "main.gob");
            File.WriteAllBytes(gobPath, gob.Append((byte)0).ToArray()); // one byte too long
            File.WriteAllBytes(Path.Combine(dir, "main.gfc"), gfc);

            Assert.False(GobArchive.IsGobArchive(gobPath));
            Assert.Null(ArchiveFileSystem.TryOpen(gobPath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetIndexPath_SwapsTheExtension()
    {
        Assert.Equal(
            Path.Combine("a", "b", "main.gfc"),
            GobArchive.GetIndexPath(Path.Combine("a", "b", "main.gob")));
        Assert.Equal("mainUS.gfc", GobArchive.GetIndexPath("mainUS.gob"));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's American Sk8land (2005-11-15, DS - Final)",
        "Tony Hawk's American Sk8land (USA).nds", "vvobj/generated/gob/main.gob", 14606)]
    [InlineData("Tony Hawk's Downhill Jam (2006-10-24, DS - Final)",
        "Tony Hawk's Downhill Jam (USA).nds", "vvobj/generated/gob/main.gob", 4657)]
    [InlineData("Tony Hawk's Proving Ground (2007-10-15, DS - Final)",
        "Tony Hawk's Proving Ground (USA).nds", "gob/mainUS.gob", 5665)]
    public void RealCart_OpensItsGobInPlace(string build, string rom, string gobPath, int fileCount)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(cart);

        var entry = cart!.FindByPath(gobPath);
        Assert.NotNull(entry);

        using var gob = cart.TryOpenNested(entry!);
        Assert.NotNull(gob);
        Assert.Equal(ArchiveAssetType.Gob, gob!.Type);
        Assert.Equal(fileCount, gob.Entries.Count);
        Assert.Equal(1, gob.NestingDepth);

        // Reading through the nested filesystem must produce the declared size.
        var sample = gob.Entries.First(e => e.Size is > 0 and < 64 * 1024);
        Assert.Equal(sample.Size, gob.ReadEntry(sample).Length);

        // The container's own names come through where they are proven.
        var named = gob.FindByPath("DefaultConfig.xml");
        Assert.NotNull(named);
        Assert.Equal(named!.Size, gob.ReadEntry(named).Length);
    }

    /// <summary>One stored chunk, one file — the smallest legal pair.</summary>
    private static (byte[] Gfc, byte[] Gob) BuildPair()
    {
        var gob = new byte[Payload.Length];
        Payload.CopyTo(gob, 0);

        var gfc = new byte[16 + 20 + 12];
        BinaryPrimitives.WriteUInt32BigEndian(gfc, GobIndex.Magic);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(4), (uint)gob.Length);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(12), 1);

        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(16), (uint)gob.Length);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(20), 0);
        BinaryPrimitives.WriteUInt16BigEndian(gfc.AsSpan(26), GobIndex.ChainEnd);
        gfc[28] = GobCodec.Stored;
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(32), GobCodec.Adler0(gob));

        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(36), 0xDEADBEEF);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(40), (uint)Payload.Length);
        BinaryPrimitives.WriteUInt32BigEndian(gfc.AsSpan(44), 0);
        return (gfc, gob);
    }
}
