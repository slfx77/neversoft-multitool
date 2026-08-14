using NeversoftMultitool.Core.Formats.Mesh.Ddm;
using AlphaMode = SharpGLTF.Schema2.AlphaMode;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ddm;

public sealed class GltfWriterMaterialCacheTests
{
    private const string SharedTextureName = "No_Texture_Map";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildDdmModel_SameTextureForOpaqueAndCutout_KeepsBothAlphaModes(
        bool cutoutFirst)
    {
        var materials = cutoutFirst
            ? new[] { ("cutout", 2u), ("opaque", 0u) }
            : new[] { ("opaque", 0u), ("cutout", 2u) };

        var (model, triangles) = GltfWriter.BuildDdmModel(CreateDdm(materials));

        Assert.Equal(2, triangles);
        Assert.Equal(2, model.LogicalMaterials.Count);

        var byName = model.LogicalMaterials.ToDictionary(material => material.Name);
        Assert.Equal(AlphaMode.OPAQUE, byName["opaque"].Alpha);
        Assert.Equal(AlphaMode.MASK, byName["cutout"].Alpha);
    }

    [Fact]
    public void BuildDdmModel_SameTextureWithinAdditiveFamily_DeduplicatesMaterial()
    {
        var ddm = CreateDdm(
            [("additive_one", 1u), ("additive_three", 3u)]);

        var (model, triangles) = GltfWriter.BuildDdmModel(ddm);

        Assert.Equal(2, triangles);
        Assert.Equal(AlphaMode.BLEND, Assert.Single(model.LogicalMaterials).Alpha);
    }

    private static DdmFile CreateDdm((string Name, uint BlendMode)[] materials)
    {
        return new DdmFile
        {
            Objects = materials
                .Select((material, index) => CreateObject(index, material.Name, material.BlendMode))
                .ToList()
        };
    }

    private static DdmObject CreateObject(int index, string materialName, uint blendMode)
    {
        return new DdmObject
        {
            Name = $"object_{index}",
            BBoxExtentX = 2f,
            BBoxExtentY = 2f,
            BBoxExtentZ = 2f,
            Materials =
            [
                new DdmMaterial
                {
                    Name = materialName,
                    TextureName = SharedTextureName,
                    DiffuseR = 255,
                    DiffuseG = 255,
                    DiffuseB = 255,
                    DiffuseA = 255,
                    BlendMode = blendMode
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
        };
    }
}
