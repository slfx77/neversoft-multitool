using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxTextureProviderFactoryTests
{
    [Fact]
    public void FromFile_FilenameOnlyPath_CreatesResolver()
    {
        var inputName = $"nmt-psx-{Guid.NewGuid():N}.psx";

        var resolver = PsxTextureProviderFactory.FromFile(inputName);

        Assert.NotNull(resolver);
    }
}
