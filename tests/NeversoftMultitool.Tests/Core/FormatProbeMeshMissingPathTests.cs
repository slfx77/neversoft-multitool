using NeversoftMultitool.Core;

namespace NeversoftMultitool.Tests.Core;

public sealed class FormatProbeMeshMissingPathTests
{
    [Theory]
    [InlineData(".ddm", "DDM Mesh")]
    [InlineData(".geom.ps2", "PS2 GEOM")]
    [InlineData(".psx.n64", "N64 Model")]
    public void ProbeMesh_MissingNameOnlyRoute_IsUnsupported(string suffix, string expectedFormat)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-missing-mesh-{Guid.NewGuid():N}{suffix}");

        Assert.False(File.Exists(path));

        var result = FormatProbe.ProbeMesh(path);

        Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
        Assert.Equal(expectedFormat, result.FormatName);
        Assert.Equal("File not found", result.UnsupportedReason);
    }

    [Theory]
    [InlineData(".ddm", "DDM Mesh")]
    [InlineData(".geom.ps2", "PS2 GEOM")]
    [InlineData(".psx.n64", "N64 Model")]
    public void ProbeMesh_ExistingNameOnlyRoute_RemainsSupported(string suffix, string expectedFormat)
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-existing-mesh-{Guid.NewGuid():N}{suffix}");
        try
        {
            File.WriteAllBytes(path, [0x00]);

            var result = FormatProbe.ProbeMesh(path);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal(expectedFormat, result.FormatName);
            Assert.Null(result.UnsupportedReason);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
