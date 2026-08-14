using System.Text;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skeleton;

public sealed class ThawSkeletonDiscoveryArchiveOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FindInArchive_ConflictingExactSkeletons_FailClosedInEitherOrder(bool reverse)
    {
        var first = (Name: "models/a/target.ske.ps2", Data: new byte[] { 0x11 });
        var second = (Name: "models/b/target.ske.ps2", Data: new byte[] { 0x22 });
        var ordered = reverse ? new[] { second, first } : new[] { first, second };
        using var fixture = OpenWad(
            ordered[0],
            ordered[1],
            ("human.ske.ps2", new byte[] { 0x33 }));

        var result = ThawSkeletonDiscovery.FindInArchive(
            fixture.Backend.Entries,
            fixture.Backend,
            "target",
            isThawSkin: true);

        Assert.Null(result);
    }

    [Fact]
    public void FindInArchive_IdenticalExactSkeletons_RemainAccepted()
    {
        byte[] skeleton = [0x11, 0x22];
        using var fixture = OpenWad(
            ("models/a/target.ske.ps2", skeleton),
            ("models/b/target.ske.ps2", skeleton));

        var result = ThawSkeletonDiscovery.FindInArchive(
            fixture.Backend.Entries,
            fixture.Backend,
            "target",
            isThawSkin: true);

        Assert.NotNull(result);
        Assert.Equal(skeleton, result.Value.Bytes);
        Assert.Equal("models/a/target.ske.ps2", result.Value.EntryName);
    }

    [Fact]
    public void FindInArchive_UniqueExactSkeleton_RemainsAccepted()
    {
        byte[] skeleton = [0x44, 0x55];
        using var fixture = OpenWad(("models/target.ske.ps2", skeleton));

        var result = ThawSkeletonDiscovery.FindInArchive(
            fixture.Backend.Entries,
            fixture.Backend,
            "target",
            isThawSkin: false);

        Assert.NotNull(result);
        Assert.Equal(skeleton, result.Value.Bytes);
        Assert.Equal("models/target.ske.ps2", result.Value.EntryName);
    }

    private static WadFixture OpenWad(params (string Name, byte[] Data)[] files)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(),
            "NsMultitool_Test_ThawSkeleton_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        using var wad = new MemoryStream();
        using var hed = new MemoryStream();
        using (var writer = new BinaryWriter(hed, Encoding.ASCII, leaveOpen: true))
        {
            foreach (var (name, data) in files)
            {
                var offset = checked((uint)wad.Length);
                wad.Write(data);

                writer.Write(Encoding.ASCII.GetBytes(name + "\0"));
                writer.Write(new byte[(4 - hed.Length % 4) % 4]);
                writer.Write(offset);
                writer.Write(checked((uint)data.Length));
            }

            writer.Write((byte)0xFF);
        }

        var wadPath = Path.Combine(tempDir, "TEST.WAD");
        File.WriteAllBytes(wadPath, wad.ToArray());
        File.WriteAllBytes(Path.Combine(tempDir, "TEST.HED"), hed.ToArray());
        var backend = ArchiveAssetBackend.TryOpen(wadPath);
        Assert.NotNull(backend);
        return new WadFixture(backend!, tempDir);
    }

    private sealed class WadFixture(ArchiveAssetBackend backend, string tempDir) : IDisposable
    {
        public ArchiveAssetBackend Backend { get; } = backend;

        public void Dispose()
        {
            Backend.FileSystem.Dispose();
            Directory.Delete(tempDir, true);
        }
    }
}
