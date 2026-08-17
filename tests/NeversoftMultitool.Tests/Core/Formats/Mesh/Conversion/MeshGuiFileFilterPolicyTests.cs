using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class MeshGuiFileFilterPolicyTests
{
    [Fact]
    public void Matches_EmptyFilterAdmitsEverything()
    {
        Assert.True(MeshGuiFileFilterPolicy.Matches("a/b.psx", "b.psx", ""));
        Assert.True(MeshGuiFileFilterPolicy.Matches("a/b.psx", "b.psx", null));
        Assert.True(MeshGuiFileFilterPolicy.Matches("", "", ""));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveOverTheRelativePath()
    {
        Assert.True(MeshGuiFileFilterPolicy.Matches(
            @"DATAP.WAD::worlds/worldzones/z_bh/Z_BH.pak.ps2", "Z_BH.pak.ps2", "z_bh"));
        Assert.True(MeshGuiFileFilterPolicy.Matches(
            @"models\skater_secret\sec_jimbo_xen.skin.ps2", "sec_jimbo_xen.skin.ps2", "JIMBO"));
        Assert.False(MeshGuiFileFilterPolicy.Matches(
            @"models\skater_secret\sec_jimbo_xen.skin.ps2", "sec_jimbo_xen.skin.ps2", "hawk"));
    }

    [Fact]
    public void Matches_MatchesDirectorySegmentsNotJustTheLeafName()
    {
        // The display column shows RelativePath, so a directory or archive
        // segment is a legitimate way to narrow the list.
        Assert.True(MeshGuiFileFilterPolicy.Matches(
            "sk5ed.pak.ps2::sk5ed_light.psx", "sk5ed_light.psx", "pak.ps2::"));
    }

    [Fact]
    public void Matches_FallsBackToFileNameWhenRelativePathIsEmpty()
    {
        Assert.True(MeshGuiFileFilterPolicy.Matches("", "hawk.psx", "hawk"));
        Assert.True(MeshGuiFileFilterPolicy.Matches(null, "hawk.psx", "hawk"));
        Assert.False(MeshGuiFileFilterPolicy.Matches(null, "hawk.psx", "spider"));
        Assert.False(MeshGuiFileFilterPolicy.Matches(null, null, "spider"));
    }

    [Fact]
    public void ConvertButtonLabel_CountsAllCheckedAndCallsOutHiddenOnes()
    {
        Assert.Equal("Convert files", MeshGuiFileFilterPolicy.ConvertButtonLabel(0, 0));
        Assert.Equal("Convert 1 file", MeshGuiFileFilterPolicy.ConvertButtonLabel(1, 0));
        Assert.Equal("Convert 12 files", MeshGuiFileFilterPolicy.ConvertButtonLabel(12, 0));
        Assert.Equal("Convert 12 files (3 hidden)", MeshGuiFileFilterPolicy.ConvertButtonLabel(12, 3));
        Assert.Equal("Convert 1 file (1 hidden)", MeshGuiFileFilterPolicy.ConvertButtonLabel(1, 1));
    }
}
