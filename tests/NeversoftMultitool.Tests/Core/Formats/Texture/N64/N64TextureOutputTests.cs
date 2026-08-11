using NeversoftMultitool.Core.Formats.Texture.N64;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Texture.N64;

public sealed class N64TextureOutputTests
{
    [Fact]
    public void WritePngLevels_PreservesTopNameAndAddsDeterministicMipNames()
    {
        using var temp = new TempDirectory();
        var topRgba = new byte[]
        {
            255, 0, 0, 255,
            0, 255, 0, 255,
            0, 0, 255, 255,
            255, 255, 255, 255
        };
        var mipRgba = new byte[] { 17, 34, 51, 255 };
        var texture = new N64TexFile.N64Texture(
            "unit",
            2,
            2,
            "RGBA16",
            topRgba,
            [
                new N64TexFile.N64MipLevel(0, 2, 2, topRgba),
                new N64TexFile.N64MipLevel(1, 1, 1, mipRgba)
            ]);
        var topPath = Path.Combine(temp.Path, "0012_unit.icon.png");

        var paths = N64TextureOutput.WritePngLevels(texture, topPath);

        Assert.Equal(
            [topPath, Path.Combine(temp.Path, "0012_unit.icon_mip1.png")],
            paths);
        using var top = Image.Load<Rgba32>(paths[0]);
        using var mip = Image.Load<Rgba32>(paths[1]);
        Assert.Equal((2, 2), (top.Width, top.Height));
        Assert.Equal((1, 1), (mip.Width, mip.Height));
        Assert.Equal(new Rgba32(255, 0, 0, 255), top[0, 0]);
        Assert.Equal(new Rgba32(17, 34, 51, 255), mip[0, 0]);
    }

    [Fact]
    public void WritePngLevels_LegacySingleLevelTextureWritesOnlyHistoricalPath()
    {
        using var temp = new TempDirectory();
        var rgba = new byte[] { 1, 2, 3, 255 };
        var texture = new N64TexFile.N64Texture("unit", 1, 1, "I8", rgba);
        var topPath = Path.Combine(temp.Path, "0021_unit.png");

        var paths = N64TextureOutput.WritePngLevels(texture, topPath);

        Assert.Equal([topPath], paths);
        Assert.True(File.Exists(topPath));
        Assert.False(File.Exists(Path.Combine(temp.Path, "0021_unit_mip1.png")));
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtN64TextureOutput_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
