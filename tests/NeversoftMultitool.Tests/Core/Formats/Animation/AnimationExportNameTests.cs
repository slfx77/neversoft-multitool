using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

public sealed class AnimationExportNameTests
{
    [Theory]
    [InlineData("anim_1", "bruce_anim_1")]
    [InlineData("anim_007", "bruce_anim_007")]
    [InlineData("anim_x", "bruce_anim_x")]
    [InlineData("ANIM_2", "bruce_ANIM_2")]
    [InlineData("sk2anim.psx::anim_1", "bruce_anim_1")]
    [InlineData("archive.wad::characters/anim_3", "bruce_anim_3")]
    public void ForMesh_PrefixesSyntheticUnnamedSlots(string animationName, string expected)
    {
        Assert.Equal(expected, AnimationExportName.ForMesh("bruce", animationName));
    }

    [Theory]
    [InlineData("idle")]
    [InlineData("anim_idle")]
    [InlineData("bruce_anim_1")]
    public void ForMesh_PreservesAuthoredOrAlreadyQualifiedNames(string animationName)
    {
        Assert.Equal(animationName, AnimationExportName.ForMesh("bruce", animationName));
    }

    [Fact]
    public void ForMesh_UsesOnlyMeshFileNameAndDisambiguatesDuplicateSlots()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.Equal("bruce_anim_1", AnimationExportName.ForMesh("levels/characters/bruce", "anim_1", used));
        Assert.Equal("bruce_anim_1_2", AnimationExportName.ForMesh("bruce", "bank.psx::anim_1", used));
    }
}