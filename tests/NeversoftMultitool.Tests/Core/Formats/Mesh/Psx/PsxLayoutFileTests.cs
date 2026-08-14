using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxLayoutFileTests
{
    [Fact]
    public void Parse_TruncatedRecognizedHeader_ReturnsNull()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nmt-psx-layout-{Guid.NewGuid():N}.psx");
        try
        {
            File.WriteAllBytes(path, [0x03, 0x00, 0x02, 0x00]);

            Assert.Null(PsxLayoutFile.Parse(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
