using System.Numerics;
using System.IO.Compression;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ModelExportServiceTests
{
    [Fact]
    public void Export_Glb_WritesSyntheticTriangle()
    {
        using var temp = new TempDirectory();
        var document = CreateTriangleDocument();

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(1, result.Triangles);
        Assert.Equal(1, result.MaterialCount);
    }

    [Fact]
    public void Export_Glb_EmbedsTextureSamplerAndAlphaMode()
    {
        using var temp = new TempDirectory();
        var document = CreateTriangleDocument();
        document.Textures.Add(new ModelTexture
        {
            Name = "unit_texture",
            PngBytes = CreatePngBytes(new Rgba32(10, 20, 30, 128)),
            WrapU = ModelTextureWrap.ClampToEdge,
            WrapV = ModelTextureWrap.Repeat
        });
        document.Materials[0].TextureIndex = 0;
        document.Materials[0].AlphaMode = ModelAlphaMode.Blend;

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        var (jsonBytes, binBytes) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var root = gltf.RootElement;

        Assert.Equal(1, result.TextureCount);
        Assert.Equal(1, root.GetProperty("images").GetArrayLength());
        Assert.Equal(1, root.GetProperty("textures").GetArrayLength());

        var material = root.GetProperty("materials")[0];
        Assert.Equal("BLEND", material.GetProperty("alphaMode").GetString());
        Assert.Equal(0, material
            .GetProperty("pbrMetallicRoughness")
            .GetProperty("baseColorTexture")
            .GetProperty("index")
            .GetInt32());

        var texture = root.GetProperty("textures")[0];
        var samplerIndex = texture.GetProperty("sampler").GetInt32();
        var sampler = root.GetProperty("samplers")[samplerIndex];
        Assert.Equal(33071, ReadSamplerWrap(sampler, "wrapS"));
        Assert.Equal(10497, ReadSamplerWrap(sampler, "wrapT"));

        var pixel = ReadEmbeddedImageFirstPixel(root, binBytes);
        Assert.Equal(new Rgba32(10, 20, 30, 128), pixel);
    }

    [Fact]
    public void Export_Glb_UsesPs2AdditiveTextureApproximationWithoutMutatingDocument()
    {
        using var temp = new TempDirectory();
        var originalPixel = new Rgba32(128, 64, 0, 255);
        var document = CreateTriangleDocument();
        document.Textures.Add(new ModelTexture
        {
            Name = "additive_texture",
            PngBytes = CreatePngBytes(originalPixel)
        });
        document.Materials[0].TextureIndex = 0;
        document.Materials[0].AlphaMode = ModelAlphaMode.Blend;
        document.Materials[0].NativeMetadata.Add(MakePs2GsMetadata(alpha: 0x48));

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        var (jsonBytes, binBytes) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var pixel = ReadEmbeddedImageFirstPixel(gltf.RootElement, binBytes);

        Assert.Equal(new Rgba32(255, 127, 0, 128), pixel);
        Assert.Equal(originalPixel, ReadPngFirstPixel(document.Textures[0].PngBytes!));
    }

    [Fact]
    public void Export_Glb_UsesPs2SubtractiveTextureApproximationWithoutMutatingDocument()
    {
        using var temp = new TempDirectory();
        var originalPixel = new Rgba32(100, 200, 50, 255);
        var document = CreateTriangleDocument();
        document.Textures.Add(new ModelTexture
        {
            Name = "subtractive_texture",
            PngBytes = CreatePngBytes(originalPixel)
        });
        document.Materials[0].TextureIndex = 0;
        document.Materials[0].AlphaMode = ModelAlphaMode.Blend;
        document.Materials[0].NativeMetadata.Add(MakePs2GsMetadata(alpha: 0x42));

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        var (jsonBytes, binBytes) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var pixel = ReadEmbeddedImageFirstPixel(gltf.RootElement, binBytes);

        Assert.Equal(new Rgba32(0, 0, 0, 60), pixel);
        Assert.Equal(originalPixel, ReadPngFirstPixel(document.Textures[0].PngBytes!));
    }

    [Fact]
    public void Export_BlendWithoutHelper_FailsWithActionableError()
    {
        using var temp = new TempDirectory();
        var document = CreateTriangleDocument();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            ModelExportService.Export(
                document,
                new MeshExportRequest
                {
                    OutputDirectory = temp.Path,
                    Format = MeshOutputFormat.Blend,
                    BlenderHelperPath = Path.Combine(temp.Path, "missing-blender.exe")
                }));

        Assert.Contains("Blender export helper was not found", ex.Message);
    }

    [Fact]
    public void Export_Blend_WithConfiguredHelper_WritesBlend()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var scriptPath = Path.Combine(AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath) || !File.Exists(helperPath) || !File.Exists(scriptPath))
            Assert.Skip("Set NEVERSOFT_BLENDER_HELPER and ensure BlenderExporter/import_package.py is copied to run this smoke test.");

        using var temp = new TempDirectory();
        var document = CreateTriangleDocument();

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Blend,
                BlenderHelperPath = helperPath
            });

        var outputPath = Assert.Single(result.OutputPaths);
        Assert.True(File.Exists(outputPath));
        Assert.Equal(1, result.Triangles);
    }

    [Fact]
    public void BlendManifest_PreservesTypedNativeMaterialMetadata()
    {
        var document = CreateTriangleDocument();
        document.Materials[0].NativeMetadata.Add(new Ps2GsRenderMetadata(
            Alpha: 0x44,
            Test: 0x13,
            Tex0: 0x1234,
            Tex1: null,
            Texa: 0x80,
            Clamp: 0x05,
            TextureChecksum: 0xDEADBEEF,
            GroupChecksum: 0xCAFEBABE,
            AlphaRef: 64,
            Source: "unit",
            Frame: 0xFF000000000A0000));

        var manifest = BlendPackageManifest.FromDocument(document, "triangle.blend");

        var metadata = Assert.Single(Assert.Single(manifest.Materials).NativeMetadata);
        Assert.Equal("ps2_gs", metadata["kind"]);
        Assert.Equal(0x44UL, metadata["alpha"]);
        Assert.Equal(0xFF000000000A0000UL, metadata["frame"]);
        Assert.Equal(0xDEADBEEFu, metadata["textureChecksum"]);
        Assert.Equal(64, metadata["alphaRef"]);
    }

    [Fact]
    public void BlendPackageWriter_WritesDirectArchivePayloadWithoutGlbManifestEntry()
    {
        var document = CreateTriangleDocument();
        document.Textures.Add(new ModelTexture
        {
            Name = "unit_texture",
            PngBytes = CreatePngBytes(new Rgba32(10, 20, 30, 255))
        });
        document.Materials[0].TextureIndex = 0;
        using var payload = new MemoryStream();

        BlendPackageWriter.Write(
            document,
            payload,
            Path.Combine(Path.GetTempPath(), "triangle.blend"));

        payload.Position = 0;
        using var archive = new ZipArchive(payload, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);
        using var manifestStream = manifestEntry.Open();
        using var reader = new StreamReader(manifestStream);
        var manifestJson = reader.ReadToEnd();
        Assert.DoesNotContain("GlbPath", manifestJson);
        Assert.Contains("VertexBuffer", manifestJson);
        Assert.Contains("RgbaPath", manifestJson);
        Assert.NotNull(archive.GetEntry("buffers/mesh_0000_prim_0000.vertices.bin"));
        Assert.NotNull(archive.GetEntry("buffers/mesh_0000_prim_0000.indices.bin"));
        Assert.NotNull(archive.GetEntry("textures/0000_unit_texture.rgba"));
    }

    private static ModelDocument CreateTriangleDocument()
    {
        var document = new ModelDocument { Name = "triangle" };
        document.Materials.Add(new RenderMaterial
        {
            Name = "triangle_mat",
            BaseColor = new Vector4(1f, 0.25f, 0.1f, 1f)
        });

        var mesh = new ModelMesh { Name = "triangle_mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "triangle_primitive",
            MaterialIndex = 0,
            Vertices =
            [
                new ModelVertex(
                    new Vector3(0f, 0f, 0f),
                    Vector3.UnitZ,
                    Vector4.One,
                    Vector2.Zero),
                new ModelVertex(
                    new Vector3(1f, 0f, 0f),
                    Vector3.UnitZ,
                    Vector4.One,
                    Vector2.UnitX),
                new ModelVertex(
                    new Vector3(0f, 1f, 0f),
                    Vector3.UnitZ,
                    Vector4.One,
                    Vector2.UnitY)
            ],
            Indices = [0, 1, 2]
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "triangle_node",
            MeshIndex = 0
        });
        return document;
    }

    private static Ps2GsRenderMetadata MakePs2GsMetadata(ulong alpha) =>
        new(
            Alpha: alpha,
            Test: 0,
            Tex0: null,
            Tex1: null,
            Texa: null,
            Clamp: null,
            TextureChecksum: null,
            GroupChecksum: null,
            AlphaRef: null,
            Source: "unit");

    private static byte[] CreatePngBytes(Rgba32 color)
    {
        using var image = new Image<Rgba32>(1, 1);
        image[0, 0] = color;
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    private static Rgba32 ReadPngFirstPixel(byte[] pngBytes)
    {
        using var image = Image.Load<Rgba32>(pngBytes);
        return image[0, 0];
    }

    private static int ReadSamplerWrap(JsonElement sampler, string propertyName) =>
        sampler.TryGetProperty(propertyName, out var wrap) ? wrap.GetInt32() : 10497;

    private static Rgba32 ReadEmbeddedImageFirstPixel(JsonElement root, byte[] binBytes)
    {
        var image = root.GetProperty("images")[0];
        var bufferViewIndex = image.GetProperty("bufferView").GetInt32();
        var bufferView = root.GetProperty("bufferViews")[bufferViewIndex];
        var offset = bufferView.TryGetProperty("byteOffset", out var byteOffset)
            ? byteOffset.GetInt32()
            : 0;
        var length = bufferView.GetProperty("byteLength").GetInt32();
        var pngBytes = binBytes.AsSpan(offset, length).ToArray();
        using var png = Image.Load<Rgba32>(pngBytes);
        return png[0, 0];
    }

    private static (byte[] Json, byte[] Bin) ReadGlbChunks(string glbPath)
    {
        using var stream = File.OpenRead(glbPath);
        using var reader = new BinaryReader(stream);
        Assert.Equal(0x46546C67u, reader.ReadUInt32());
        _ = reader.ReadUInt32();
        _ = reader.ReadUInt32();

        var jsonLength = reader.ReadUInt32();
        Assert.Equal(0x4E4F534Au, reader.ReadUInt32());
        var jsonBytes = reader.ReadBytes(checked((int)jsonLength));

        var binLength = reader.ReadUInt32();
        Assert.Equal(0x004E4942u, reader.ReadUInt32());
        var binBytes = reader.ReadBytes(checked((int)binLength));
        return (jsonBytes, binBytes);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                    Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup is best-effort.
            }
        }
    }
}
