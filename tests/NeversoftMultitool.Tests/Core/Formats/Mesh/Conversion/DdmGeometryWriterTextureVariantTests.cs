using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class DdmGeometryWriterTextureVariantTests
{
    private static readonly Rgba32 SourcePixel = new(64, 128, 192, 255);
    private static readonly Rgba32 AdditivePixel = new(255, 255, 255, 116);

    [Fact]
    public void PopulateDdmPlacedLevel_SameNameRawAndAdditiveTexturesRemainDistinct()
    {
        var document = PopulateDdmPlacedLevel(0, 1);

        var rawIndex = Assert.IsType<int>(document.Materials[0].TextureIndex);
        var additiveIndex = Assert.IsType<int>(document.Materials[1].TextureIndex);
        Assert.NotEqual(rawIndex, additiveIndex);

        Assert.Equal(2, document.Textures.Count);
        Assert.Equal("shared", document.Textures[rawIndex].Name);
        Assert.Equal("shared", document.Textures[additiveIndex].Name);
        AssertPixel(document.Textures[rawIndex].PngBytes, SourcePixel);
        AssertPixel(document.Textures[additiveIndex].PngBytes, AdditivePixel);
    }

    [Fact]
    public void PopulateDdmPlacedLevel_SameNameIdenticalAdditiveTexturesRemainShared()
    {
        var document = PopulateDdmPlacedLevel(1, 3);

        var firstIndex = Assert.IsType<int>(document.Materials[0].TextureIndex);
        var secondIndex = Assert.IsType<int>(document.Materials[1].TextureIndex);
        Assert.Equal(firstIndex, secondIndex);

        var texture = Assert.Single(document.Textures);
        Assert.Equal("shared", texture.Name);
        AssertPixel(texture.PngBytes, AdditivePixel);
    }

    private static ModelDocument PopulateDdmPlacedLevel(uint firstBlendMode, uint secondBlendMode)
    {
        var temp = Path.Combine(Path.GetTempPath(), "NeversoftMultitoolTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);

        try
        {
            using (var image = new Image<Rgba32>(1, 1))
            {
                image[0, 0] = SourcePixel;
                image.SaveAsPng(Path.Combine(temp, "shared.png"));
            }

            var document = new ModelDocument
            {
                Name = "ddm_texture_variants",
                SourceKind = ModelSourceKind.DdmPlacedLevel
            };
            DdmGeometryWriter.PopulateDdmPlacedLevel(
                document,
                CreateDdmFile(firstBlendMode, secondBlendMode),
                null,
                null,
                null,
                null,
                [temp]);
            return document;
        }
        finally
        {
            Directory.Delete(temp, recursive: true);
        }
    }

    private static DdmFile CreateDdmFile(uint firstBlendMode, uint secondBlendMode)
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
                        CreateMaterial("raw", firstBlendMode),
                        CreateMaterial("additive", secondBlendMode)
                    ],
                    Vertices =
                    [
                        new DdmVertex(0f, 0f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 0f, 0f),
                        new DdmVertex(1f, 0f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 1f, 0f),
                        new DdmVertex(0f, 1f, 0f, 0f, 0f, 1f, 255, 255, 255, 255, 0f, 1f)
                    ],
                    Indices = [0, 1, 2],
                    Splits =
                    [
                        new DdmSplit(0, 0, 3),
                        new DdmSplit(1, 0, 3)
                    ]
                }
            ]
        };
    }

    private static DdmMaterial CreateMaterial(string name, uint blendMode)
    {
        return new DdmMaterial
        {
            Name = name,
            TextureName = "shared",
            DiffuseR = 255,
            DiffuseG = 255,
            DiffuseB = 255,
            DiffuseA = 255,
            BlendMode = blendMode
        };
    }

    private static void AssertPixel(byte[]? pngBytes, Rgba32 expected)
    {
        Assert.NotNull(pngBytes);
        using var image = Image.Load<Rgba32>(pngBytes);
        Assert.Equal(1, image.Width);
        Assert.Equal(1, image.Height);
        Assert.Equal(expected, image[0, 0]);
    }
}
