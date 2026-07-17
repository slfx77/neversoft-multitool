using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class GltfModelExporterTests
{
    [Fact]
    public void BuildGlbBytes_StoresVertexColorsAsNormalizedUnsignedShort()
    {
        var document = CreateTriangleDocument();

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);

        Assert.Equal(1, triangles);
        Assert.NotNull(glbBytes);
        using var json = ReadGlbJson(glbBytes);
        var root = json.RootElement;
        var colorAccessorIndex = root
            .GetProperty("meshes")[0]
            .GetProperty("primitives")[0]
            .GetProperty("attributes")
            .GetProperty("COLOR_0")
            .GetInt32();
        var colorAccessor = root.GetProperty("accessors")[colorAccessorIndex];

        Assert.Equal(5123, colorAccessor.GetProperty("componentType").GetInt32());
        Assert.True(colorAccessor.GetProperty("normalized").GetBoolean());
        Assert.Equal("VEC4", colorAccessor.GetProperty("type").GetString());
    }

    private static ModelDocument CreateTriangleDocument()
    {
        var document = new ModelDocument { Name = "high_precision_vertex_color" };
        var mesh = new ModelMesh { Name = "triangle" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "triangle",
            Vertices =
            [
                new ModelVertex(
                    Vector3.Zero,
                    Vector3.UnitZ,
                    new Vector4(0.004f, 0.008f, 0.012f, 1f),
                    Vector2.Zero),
                new ModelVertex(
                    Vector3.UnitX,
                    Vector3.UnitZ,
                    new Vector4(0.016f, 0.020f, 0.024f, 1f),
                    Vector2.UnitX),
                new ModelVertex(
                    Vector3.UnitY,
                    Vector3.UnitZ,
                    new Vector4(0.028f, 0.032f, 0.036f, 1f),
                    Vector2.UnitY)
            ],
            Indices = [0, 1, 2]
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "triangle",
            MeshIndex = 0
        });
        return document;
    }

    private static JsonDocument ReadGlbJson(byte[] glbBytes)
    {
        using var stream = new MemoryStream(glbBytes, writable: false);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        Assert.Equal(2u, reader.ReadUInt32());
        Assert.Equal(checked((uint)glbBytes.Length), reader.ReadUInt32());

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        return JsonDocument.Parse(reader.ReadBytes(checked((int)jsonLength)));
    }
}
