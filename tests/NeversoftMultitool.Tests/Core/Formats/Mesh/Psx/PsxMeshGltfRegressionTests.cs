using System.Text.Json;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxMeshGltfRegressionTests(TestPaths paths)
{
    [Fact]
    public void Write_Hawk2_TexturedGlb_ContainsMaskedMaterialsForCutouts()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var inputFile = RequireSampleBuildFile(
            @"Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)\PSX\HAWK2.PSX");
        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.NotNull(psxFile);

        var outputFile = ExportTexturedGlb(psxFile, inputFile, "hawk2_mask_regression");
        var json = LoadGlbJson(outputFile);

        var alphaModes = json.RootElement
            .GetProperty("materials")
            .EnumerateArray()
            .Select(material => material.TryGetProperty("alphaMode", out var alphaMode)
                ? alphaMode.GetString() ?? "OPAQUE"
                : "OPAQUE")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("MASK", alphaModes);
    }

    [Fact]
    public void Write_BlackcatV6_TexturedGlb_KeepsUvCoordinatesNonNegative()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var inputFile = RequireSampleBuildFile(
            @"Spider-Man (2001-2-14, DC - Prototype)\PSX\BLACKCAT.PSX");
        var psxFile = PsxMeshFile.Parse(inputFile);
        Assert.NotNull(psxFile);

        var outputFile = ExportTexturedGlb(psxFile, inputFile, "blackcat_v6_uv_regression");
        var (json, binChunk) = LoadGlb(outputFile);
        var minU = EnumerateTexCoords(json.RootElement, binChunk).Min(texCoord => texCoord.U);
        var minV = EnumerateTexCoords(json.RootElement, binChunk).Min(texCoord => texCoord.V);

        Assert.True(minU >= 0f, $"Expected non-mirrored U coordinates, but minimum U was {minU}");
        Assert.True(minV >= 0f, $"Expected non-inverted V coordinates, but minimum V was {minV}");
    }

    private string RequireSampleBuildFile(string relativePath)
    {
        var filePath = Path.Combine(paths.SampleBuildsDir!, relativePath);
        Assert.SkipWhen(!File.Exists(filePath), $"Fixture not found: {relativePath}");
        return filePath;
    }

    private static string ExportTexturedGlb(PsxMeshFile psxFile, string inputFile, string tempStem)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "NsMultitool_Test_PsxGlb_" + tempStem);
        Directory.CreateDirectory(tempDir);

        var outputFile = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(inputFile) + ".glb");
        var pshFile = psxFile.HasHierarchy ? PshFile.FindCompanion(inputFile) : null;

        MeshChecksumTextureResolver textureProvider = hash =>
        {
            var result = PsxLibrary.ExtractTextureByHash(inputFile, hash);
            if (result == null) return null;
            var (rgba, width, height) = result.Value;
            return ImageWriter.WritePngToMemory(width, height, rgba);
        };

        PsxGltfWriter.Write(psxFile, outputFile, textureProvider, pshFile);
        return outputFile;
    }

    private static JsonDocument LoadGlbJson(string glbPath)
    {
        var (json, _) = LoadGlb(glbPath);
        return json;
    }

    private static (JsonDocument Json, byte[] BinChunk) LoadGlb(string glbPath)
    {
        using var stream = File.OpenRead(glbPath);
        using var reader = new BinaryReader(stream);

        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        var fileLength = reader.ReadUInt32();

        JsonDocument? json = null;
        byte[]? binChunk = null;

        while (stream.Position < fileLength)
        {
            var chunkLength = reader.ReadUInt32();
            var chunkType = reader.ReadUInt32();
            var chunkData = reader.ReadBytes((int)chunkLength);

            if (chunkType == 0x4E4F534A)
                json = JsonDocument.Parse(chunkData);
            else if (chunkType == 0x004E4942)
                binChunk = chunkData;
        }

        Assert.NotNull(json);
        Assert.NotNull(binChunk);
        return (json!, binChunk!);
    }

    private static IEnumerable<(float U, float V)> EnumerateTexCoords(JsonElement root, byte[] binChunk)
    {
        var accessors = root.GetProperty("accessors");
        var bufferViews = root.GetProperty("bufferViews");

        foreach (var mesh in root.GetProperty("meshes").EnumerateArray())
        {
            foreach (var primitive in mesh.GetProperty("primitives").EnumerateArray())
            {
                if (!primitive.GetProperty("attributes").TryGetProperty("TEXCOORD_0", out var accessorIndexProperty))
                    continue;

                var accessor = accessors[accessorIndexProperty.GetInt32()];
                Assert.Equal("VEC2", accessor.GetProperty("type").GetString());
                Assert.Equal(5126, accessor.GetProperty("componentType").GetInt32());

                var count = accessor.GetProperty("count").GetInt32();
                var bufferView = bufferViews[accessor.GetProperty("bufferView").GetInt32()];
                var baseOffset = bufferView.TryGetProperty("byteOffset", out var bufferViewOffset)
                    ? bufferViewOffset.GetInt32()
                    : 0;
                var accessorOffset = accessor.TryGetProperty("byteOffset", out var accessorPropertyOffset)
                    ? accessorPropertyOffset.GetInt32()
                    : 0;
                var stride = bufferView.TryGetProperty("byteStride", out var byteStride)
                    ? byteStride.GetInt32()
                    : sizeof(float) * 2;
                var offset = baseOffset + accessorOffset;

                for (var i = 0; i < count; i++)
                {
                    var elementOffset = offset + i * stride;
                    yield return (
                        BitConverter.ToSingle(binChunk, elementOffset),
                        BitConverter.ToSingle(binChunk, elementOffset + sizeof(float)));
                }
            }
        }
    }
}
