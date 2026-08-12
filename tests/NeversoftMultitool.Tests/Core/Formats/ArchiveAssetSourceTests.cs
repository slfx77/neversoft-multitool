using System.Text;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool.Tests.Core.Formats;

public sealed class ArchiveAssetSourceTests
{
    [Fact]
    public void BackendBasenameLookup_FindsPlaintextHedEntryWhoseNameContainsPath()
    {
        var (wadPath, tempDir) = BuildWadOnDisk(
            ("pre/a/human.ske.ps2", new byte[] { 0x2A }),
            ("human.ske.ps2", new byte[] { 0x2B }),
            ("pre/b/human.ske.ps2", new byte[] { 0x2C }));
        ArchiveAssetBackend? backend = null;

        try
        {
            backend = ArchiveAssetBackend.TryOpen(wadPath);
            Assert.NotNull(backend);

            var entry = backend!.FindEntry("human.ske.ps2");
            var all = backend.FindAllByName("human.ske.ps2");

            Assert.NotNull(entry);
            Assert.Equal(
                ["pre/a/human.ske.ps2", "human.ske.ps2", "pre/b/human.ske.ps2"],
                all.Select(static match => match.Name));
            Assert.Same(all[0], entry);
            Assert.Equal(new byte[] { 0x2A }, backend.ReadEntryBytes(entry));
        }
        finally
        {
            backend?.FileSystem.Dispose();
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CompanionLookup_PrefersSelectedEntryDirectoryOverFlatFirstMatch()
    {
        var (wadPath, tempDir) = BuildWadOnDisk(
            ("models/female/shared.tex.ps2", new byte[] { 0xF0 }),
            ("models/male/shared.skin.ps2", new byte[] { 0x10 }),
            ("models/male/shared.tex.ps2", new byte[] { 0xA0 }));
        ArchiveAssetBackend? backend = null;

        try
        {
            backend = ArchiveAssetBackend.TryOpen(wadPath);
            Assert.NotNull(backend);

            var mesh = backend!.FindByPath("models/male/shared.skin.ps2");
            Assert.NotNull(mesh);
            var source = new ArchiveAssetSource(backend, mesh!);

            Assert.Equal(new byte[] { 0xA0 }, source.TryReadCompanion("shared.tex.ps2"));
            Assert.True(source.CompanionExists("shared.tex.ps2"));
        }
        finally
        {
            backend?.FileSystem.Dispose();
            Directory.Delete(tempDir, true);
        }
    }

    private static (string WadPath, string TempDir) BuildWadOnDisk(
        params (string Name, byte[] Data)[] files)
    {
        var tempDir = Path.Combine(
            Path.GetTempPath(), "NsMultitool_Test_ArchiveSource_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        using var wad = new MemoryStream();
        using var hed = new MemoryStream();
        using var hedWriter = new BinaryWriter(hed);
        foreach (var (name, data) in files)
        {
            var offset = (uint)wad.Length;
            wad.Write(data);

            hedWriter.Write(Encoding.ASCII.GetBytes(name + "\0"));
            hedWriter.Write(new byte[(4 - hed.Length % 4) % 4]);
            hedWriter.Write(offset);
            hedWriter.Write((uint)data.Length);
        }

        hedWriter.Write((byte)0xFF);
        var wadPath = Path.Combine(tempDir, "TEST.WAD");
        File.WriteAllBytes(wadPath, wad.ToArray());
        File.WriteAllBytes(Path.Combine(tempDir, "TEST.HED"), hed.ToArray());
        return (wadPath, tempDir);
    }
}
