using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal static class BlendPackageWriter
{
    private const int VertexFloatCount = 12;

    internal static int VertexStrideBytes => VertexFloatCount * sizeof(float);

    public static void Write(ModelDocument document, Stream packageStream, string blendPath)
    {
        if (!document.Nodes.Any(static node => node.MeshIndex.HasValue) ||
            document.Meshes.All(static mesh => mesh.Primitives.Count == 0))
        {
            throw new InvalidOperationException(
                $"Blend export requires ModelDocument geometry. Parser adapter for {document.SourceKind} did not populate geometry.");
        }

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, true);
        var textures = WriteTextures(document, archive);
        var meshes = WriteMeshes(document, archive);
        var nodes = document.Nodes.Select(static node => new BlendNodeManifest
        {
            Name = node.Name,
            MeshIndex = node.MeshIndex,
            Transform = ToArray(node.Transform),
            ChildNodeIndices = node.ChildNodeIndices.ToList(),
            NativeMetadata = node.NativeMetadata.Select(BlendPackageManifest.ToDictionary).ToList()
        }).ToList();

        var manifest = BlendPackageManifest.FromDocument(document, blendPath, textures, meshes, nodes);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, BlendPackageManifest.JsonOptions);
    }

    private static List<BlendTextureManifest> WriteTextures(ModelDocument document, ZipArchive archive)
    {
        var textures = new List<BlendTextureManifest>(document.Textures.Count);
        for (var i = 0; i < document.Textures.Count; i++)
        {
            var texture = document.Textures[i];
            string? rgbaPath = null;
            int? width = null;
            int? height = null;
            if (texture.PngBytes is { Length: > 0 })
            {
                using var image = Image.Load<Rgba32>(texture.PngBytes);
                var fileName = $"{i:D4}_{SanitizeFileName(texture.Name)}.rgba";
                rgbaPath = $"textures/{fileName}";
                width = image.Width;
                height = image.Height;
                var entry = archive.CreateEntry(rgbaPath, CompressionLevel.Fastest);
                using var stream = entry.Open();
                var pixels = new byte[checked(image.Width * image.Height * 4)];
                image.CopyPixelDataTo(pixels);
                stream.Write(pixels);
            }

            textures.Add(new BlendTextureManifest
            {
                Name = texture.Name,
                PngPath = null,
                RgbaPath = rgbaPath,
                Width = width,
                Height = height,
                WrapU = texture.WrapU.ToString(),
                WrapV = texture.WrapV.ToString(),
                NativeChecksum = texture.NativeChecksum
            });
        }

        return textures;
    }

    private static List<BlendMeshManifest> WriteMeshes(ModelDocument document, ZipArchive archive)
    {
        var meshes = new List<BlendMeshManifest>(document.Meshes.Count);
        for (var meshIndex = 0; meshIndex < document.Meshes.Count; meshIndex++)
        {
            var mesh = document.Meshes[meshIndex];
            var primitives = new List<BlendPrimitiveManifest>(mesh.Primitives.Count);
            for (var primitiveIndex = 0; primitiveIndex < mesh.Primitives.Count; primitiveIndex++)
            {
                var primitive = mesh.Primitives[primitiveIndex];
                var vertexFileName = $"mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.vertices.bin";
                var indexFileName = $"mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.indices.bin";
                var vertexPath = $"buffers/{vertexFileName}";
                var indexPath = $"buffers/{indexFileName}";
                WriteVertexBuffer(archive, vertexPath, primitive.Vertices);
                WriteIndexBuffer(archive, indexPath, primitive.Indices);

                primitives.Add(new BlendPrimitiveManifest
                {
                    Name = primitive.Name,
                    MaterialIndex = primitive.MaterialIndex,
                    VertexBuffer = vertexPath,
                    VertexCount = primitive.Vertices.Length,
                    IndexBuffer = indexPath,
                    IndexCount = primitive.Indices.Length,
                    TriangleCount = primitive.TriangleCount,
                    NativeMetadata = primitive.NativeMetadata.Select(BlendPackageManifest.ToDictionary).ToList()
                });
            }

            meshes.Add(new BlendMeshManifest
            {
                Name = mesh.Name,
                Primitives = primitives,
                NativeMetadata = mesh.NativeMetadata.Select(BlendPackageManifest.ToDictionary).ToList()
            });
        }

        return meshes;
    }

    private static void WriteVertexBuffer(ZipArchive archive, string path, IReadOnlyList<ModelVertex> vertices)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new BinaryWriter(stream);
        foreach (var vertex in vertices)
        {
            writer.Write(vertex.Position.X);
            writer.Write(vertex.Position.Y);
            writer.Write(vertex.Position.Z);
            writer.Write(vertex.Normal.X);
            writer.Write(vertex.Normal.Y);
            writer.Write(vertex.Normal.Z);
            writer.Write(vertex.Color.X);
            writer.Write(vertex.Color.Y);
            writer.Write(vertex.Color.Z);
            writer.Write(vertex.Color.W);
            writer.Write(vertex.TexCoord.X);
            writer.Write(vertex.TexCoord.Y);
        }
    }

    private static void WriteIndexBuffer(ZipArchive archive, string path, IReadOnlyList<int> indices)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new BinaryWriter(stream);
        foreach (var index in indices)
            writer.Write(index);
    }

    private static float[] ToArray(Matrix4x4 matrix)
    {
        return
        [
            matrix.M11, matrix.M12, matrix.M13, matrix.M14,
            matrix.M21, matrix.M22, matrix.M23, matrix.M24,
            matrix.M31, matrix.M32, matrix.M33, matrix.M34,
            matrix.M41, matrix.M42, matrix.M43, matrix.M44
        ];
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var sanitized = new string(chars).Trim();
        return string.IsNullOrWhiteSpace(sanitized)
            ? "texture"
            : sanitized.Length <= 80
                ? sanitized
                : sanitized[..80].TrimEnd('_', '.', ' ');
    }
}
