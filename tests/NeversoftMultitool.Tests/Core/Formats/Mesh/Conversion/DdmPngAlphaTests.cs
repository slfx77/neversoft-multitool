using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class DdmPngAlphaTests
{
    [Theory]
    [InlineData(128, ModelAlphaMode.Blend)]
    [InlineData(255, ModelAlphaMode.Opaque)]
    public void PopulateDdm_FilesystemPngUsesActualAlpha(
        int secondPixelAlpha,
        ModelAlphaMode expectedAlphaMode)
    {
        var temp = Path.Combine(Path.GetTempPath(), "NeversoftMultitoolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            var pngPath = Path.Combine(temp, "surface.png");
            using (var image = new Image<Rgba32>(2, 1))
            {
                image[0, 0] = new Rgba32(32, 64, 96, 255);
                image[1, 0] = new Rgba32(96, 64, 32, checked((byte)secondPixelAlpha));
                image.SaveAsPng(pngPath);
            }

            var sourcePng = File.ReadAllBytes(pngPath);
            var document = new ModelDocument { Name = "ddm_png_alpha", SourceKind = ModelSourceKind.Ddm };
            document.Materials.Add(new RenderMaterial { Name = "surface" });

            DdmGeometryWriter.PopulateDdm(document, CreateDdmFile(), null, [temp]);

            var material = Assert.Single(document.Materials);
            Assert.Equal(expectedAlphaMode, material.AlphaMode);
            Assert.Equal(0, Assert.IsType<int>(material.TextureIndex));

            var texture = Assert.Single(document.Textures);
            Assert.NotNull(texture.PngBytes);
            Assert.True(sourcePng.AsSpan().SequenceEqual(texture.PngBytes));
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static DdmFile CreateDdmFile()
    {
        return new DdmFile
        {
            Objects =
            [
                new DdmObject
                {
                    Name = "triangle",
                    Materials =
                    [
                        new DdmMaterial
                        {
                            Name = "surface",
                            TextureName = "surface",
                            DiffuseR = 255,
                            DiffuseG = 255,
                            DiffuseB = 255,
                            DiffuseA = 255,
                            BlendMode = 0
                        }
                    ],
                    Vertices =
                    [
                        new DdmVertex(0f, 0f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 0f, 0f),
                        new DdmVertex(1f, 0f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 1f, 0f),
                        new DdmVertex(0f, 1f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 0f, 1f)
                    ],
                    Indices = [0, 1, 2],
                    Splits = [new DdmSplit(0, 0, 3)]
                }
            ]
        };
    }
}
