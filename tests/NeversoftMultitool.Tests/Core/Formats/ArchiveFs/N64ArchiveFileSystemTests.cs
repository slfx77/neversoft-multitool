using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Tests.Core.Formats.ArchiveFs;

/// <summary>
///     Pins the carved-ROM filesystem backend (2026-08-06): a .z64 opens via
///     <see cref="ArchiveFileSystem.TryOpen(string)" /> as its carved asset
///     tree, entries read back byte-identical to the carver's output, and
///     texture records decode straight from the filesystem — the seam the
///     Texture tab's in-place archive browsing consumes.
/// </summary>
public sealed class N64ArchiveFileSystemTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater 2 (USA).z64";

    [Fact]
    public void TryOpen_ListsTheCarvedTree_AndReadsDecodableEntries()
    {
        var romPath = paths.FindSampleFile(Thps2N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS2 N64 ROM sample not available");

        using var fs = ArchiveFileSystem.TryOpen(romPath!);
        Assert.NotNull(fs);
        Assert.Equal(ArchiveAssetType.N64, fs!.Type);

        // Same listing the extraction paths produce.
        Assert.Equal(N64RomArchive.GetFileList(romPath!).Count, fs.Entries.Count);

        // Full-path lookup with Directory/Name split entries.
        var abutton = fs.FindByPath("textures/0000_abutton.tex.n64");
        Assert.NotNull(abutton);
        Assert.Equal("textures", abutton!.Directory);
        Assert.Equal("0000_abutton.tex.n64", abutton.Name);

        // Entry bytes are the carved asset verbatim and decode in place.
        var texture = N64TexFile.Decode(fs.ReadEntry(abutton));
        Assert.Equal("abutton", texture.Name);
        Assert.Equal(16, texture.Width);
        Assert.Equal(16, texture.Height);

        var image = fs.FindByPath("images/003.img.n64");
        Assert.NotNull(image);
        Assert.Equal(512, N64TexFile.Decode(fs.ReadEntry(image!)).Width);

        // ReadEntry hands out copies — a consumer mutation must not poison
        // later reads of the same entry.
        var first = fs.ReadEntry(abutton);
        first[0] ^= 0xFF;
        Assert.NotEqual(first[0], fs.ReadEntry(abutton)[0]);
    }
}
