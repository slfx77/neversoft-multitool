using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class Ps2WorldzoneBillboardManifestTests
{
    [Fact]
    public void BlendPackageManifest_SerializesBillboardMetadataAsJson()
    {
        // Direct serialization round-trip: prove the new ToDictionary case emits
        // billboardKind / anchor / size / pivot / axis in the shape the Blender
        // importer expects. The decoder extraction is covered by
        // Ps2GeomVifVertexDecoderTests; the end-to-end flow is validated by
        // running the converter against z_sm.pak.ps2 in the wider audit suite.
        var meta = new Ps2WorldzoneBillboardMetadata(
            BillboardKind: "LongAxis",
            AnchorX: 100f, AnchorY: 50f, AnchorZ: 200f,
            Width: 4f, Height: 12f,
            PivotX: 0f, PivotY: 0f, PivotZ: 5f,
            AxisX: 0f, AxisY: 1f, AxisZ: 0f);

        var dict = BlendPackageManifest.ToDictionary(meta);

        Assert.Equal("ps2_worldzone_billboard", dict["kind"]);
        Assert.Equal("LongAxis", dict["billboardKind"]);
        var anchor = Assert.IsType<float[]>(dict["anchor"]);
        Assert.Equal(new[] { 100f, 50f, 200f }, anchor);
        var size = Assert.IsType<float[]>(dict["size"]);
        Assert.Equal(new[] { 4f, 12f }, size);
        var pivot = Assert.IsType<float[]>(dict["pivot"]);
        Assert.Equal(new[] { 0f, 0f, 5f }, pivot);
        var axis = Assert.IsType<float[]>(dict["axis"]);
        Assert.Equal(new[] { 0f, 1f, 0f }, axis);

        // The dictionary survives JSON round-trip — the Blender script reads the
        // manifest after a JSON encode/decode through the package zip.
        var json = JsonSerializer.Serialize(dict);
        Assert.Contains("\"billboardKind\":\"LongAxis\"", json);
        Assert.Contains("\"anchor\":[100,50,200]", json);
        Assert.Contains("\"axis\":[0,1,0]", json);
    }
}
