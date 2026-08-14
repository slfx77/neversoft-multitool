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
        var hasGeometry = document.Nodes.Any(static node => node.MeshIndex.HasValue) &&
                          document.Meshes.Any(static mesh => mesh.Primitives.Count > 0);
        var hasSkeleton = document.Skeletons.Any(static skeleton => skeleton.Bones.Count > 0);
        if (!hasGeometry && !hasSkeleton)
        {
            throw new InvalidOperationException(
                $"Blend export requires ModelDocument geometry or a non-empty skeleton. " +
                $"Parser adapter for {document.SourceKind} did not populate either.");
        }

        foreach (var primitive in document.Meshes.SelectMany(static mesh => mesh.Primitives))
            ValidateCompleteTriangleIndices(primitive);

        using var archive = new ZipArchive(packageStream, ZipArchiveMode.Create, true);
        var textures = WriteTextures(document, archive);
        var (colourPulseChannels, colourPulseCodeMap) = BuildColourPulseChannels(document);
        var meshes = WriteMeshes(document, archive, colourPulseChannels, colourPulseCodeMap);
        var nodes = document.Nodes.Select(static node => new BlendNodeManifest
        {
            Name = node.Name,
            MeshIndex = node.MeshIndex,
            Transform = ToArray(node.Transform),
            ChildNodeIndices = node.ChildNodeIndices.ToList(),
            NativeMetadata = node.NativeMetadata.Select(BlendPackageManifest.ToDictionary).ToList()
        }).ToList();
        var skeletons = document.Skeletons.Select(static skeleton => new BlendSkeletonManifest
        {
            Name = skeleton.Name,
            RootTransform = ToArray(skeleton.RootTransform),
            Bones = skeleton.Bones.Select(static bone => new BlendBoneManifest
            {
                Name = bone.Name,
                ParentIndex = bone.ParentIndex,
                LocalTransform = ToArray(bone.LocalTransform),
                InverseBindMatrix = ToArray(bone.InverseBindMatrix),
                NativeChecksum = bone.NativeChecksum
            }).ToList()
        }).ToList();
        var animations = WriteAnimations(document, archive);

        var manifest =
            BlendPackageManifest.FromDocument(
                document,
                blendPath,
                textures,
                meshes,
                nodes,
                skeletons,
                animations,
                colourPulseChannels);
        var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Fastest);
        using var manifestStream = manifestEntry.Open();
        JsonSerializer.Serialize(manifestStream, manifest, BlendPackageManifest.JsonOptions);
    }

    private static void ValidateCompleteTriangleIndices(ModelPrimitive primitive)
    {
        if (primitive.Indices.Length % 3 != 0)
        {
            throw new InvalidDataException(
                $"Mesh primitive '{primitive.Name}' has {primitive.Indices.Length} indices; " +
                "triangle indices must contain complete triples.");
        }
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

    private static List<BlendMeshManifest> WriteMeshes(
        ModelDocument document,
        ZipArchive archive,
        IReadOnlyList<BlendColourPulseChannelManifest> colourPulseChannels,
        IReadOnlyDictionary<int, byte> colourPulseCodeMap)
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

                BlendSkinManifest? skin = null;
                if (primitive.Skin is { Influences: { Length: > 0 } influences })
                {
                    var skinFileName = $"mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.skin.bin";
                    var skinPath = $"buffers/{skinFileName}";
                    WriteSkinBuffer(archive, skinPath, influences);
                    skin = new BlendSkinManifest
                    {
                        SkeletonIndex = primitive.Skin.SkeletonIndex,
                        InfluenceBuffer = skinPath,
                        InfluenceCount = influences.Length
                    };
                }

                string? textureWibblePath = null;
                if (primitive.Vertices.Any(static vertex => vertex.TextureWibble.HasValue))
                {
                    var wibbleFileName = $"mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.wibble.bin";
                    textureWibblePath = $"buffers/{wibbleFileName}";
                    WriteTextureWibbleBuffer(archive, textureWibblePath, primitive.Vertices);
                }

                string? colourPulsePath = null;
                var colourPulseCodes = BuildColourPulseCodes(
                    primitive.Vertices,
                    colourPulseChannels,
                    colourPulseCodeMap);
                if (colourPulseCodes != null)
                {
                    var pulseFileName = $"mesh_{meshIndex:D4}_prim_{primitiveIndex:D4}.pulse.bin";
                    colourPulsePath = $"buffers/{pulseFileName}";
                    WriteByteBuffer(archive, colourPulsePath, colourPulseCodes);
                }

                primitives.Add(new BlendPrimitiveManifest
                {
                    Name = primitive.Name,
                    MaterialIndex = primitive.MaterialIndex,
                    VertexBuffer = vertexPath,
                    VertexCount = primitive.Vertices.Length,
                    IndexBuffer = indexPath,
                    IndexCount = primitive.Indices.Length,
                    TriangleCount = primitive.TriangleCount,
                    Skin = skin,
                    TextureWibbleBuffer = textureWibblePath,
                    ColourPulseBuffer = colourPulsePath,
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

    private static (
        List<BlendColourPulseChannelManifest> Channels,
        Dictionary<int, byte> CodeMap) BuildColourPulseChannels(ModelDocument document)
    {
        var result = new List<BlendColourPulseChannelManifest>();
        var codeMap = new Dictionary<int, byte>();
        var table = document.NativeMetadata.OfType<PsxColourPulseTableMetadata>().FirstOrDefault();
        if (table == null)
            return (result, codeMap);

        var referencedCodes = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.ColourPulseChannel)
            .Where(static code => code > 0)
            .ToHashSet();

        for (var sourceIndex = 0; sourceIndex < table.Channels.Count && result.Count < byte.MaxValue; sourceIndex++)
        {
            if (!referencedCodes.Contains(sourceIndex + 1))
                continue;

            var channel = table.Channels[sourceIndex];
            if (!TryCreateColourPulseManifest(channel, out var manifest))
                continue;

            result.Add(manifest);
            codeMap[sourceIndex + 1] = checked((byte)result.Count);
        }

        return (result, codeMap);
    }

    private static bool TryCreateColourPulseManifest(
        ModelColourPulseChannel? channel,
        out BlendColourPulseChannelManifest manifest)
    {
        manifest = null!;
        if (channel?.PortableKeys == null || channel.Intervals == null || channel.PortableKeys.Count == 0 ||
            channel.PortableKeys.Count != channel.Intervals.Count ||
            channel.PortableKeys.Count > byte.MaxValue ||
            channel.InitialKeyIndex >= channel.PortableKeys.Count ||
            channel.PortableKeys.Any(static key => !IsFinite(key)))
        {
            return false;
        }

        manifest = new BlendColourPulseChannelManifest
        {
            PortableKeys = channel.PortableKeys.Select(static key => new[] { key.X, key.Y, key.Z, key.W }).ToList(),
            Intervals = channel.Intervals.Select(static interval => (int)interval).ToList(),
            InitialKeyIndex = channel.InitialKeyIndex,
            InitialAccumulator = channel.InitialAccumulator
        };
        return true;
    }

    private static byte[]? BuildColourPulseCodes(
        IReadOnlyList<ModelVertex> vertices,
        IReadOnlyList<BlendColourPulseChannelManifest> channels,
        IReadOnlyDictionary<int, byte> codeMap)
    {
        var result = new byte[vertices.Count];
        var hasPulse = false;
        for (var i = 0; i < vertices.Count; i++)
        {
            var vertex = vertices[i];
            if (!codeMap.TryGetValue(vertex.ColourPulseChannel, out var code) ||
                code == 0 || code > channels.Count)
            {
                continue;
            }

            result[i] = code;
            hasPulse = true;
        }

        return hasPulse ? result : null;
    }

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W) &&
        value.X >= 0f && value.Y >= 0f && value.Z >= 0f && value.W >= 0f;

    private static void WriteByteBuffer(ZipArchive archive, string path, ReadOnlySpan<byte> values)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(values);
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

    private static List<BlendAnimationManifest> WriteAnimations(ModelDocument document, ZipArchive archive)
    {
        var animations = new List<BlendAnimationManifest>(document.Animations.Count);
        for (var animIndex = 0; animIndex < document.Animations.Count; animIndex++)
        {
            var animation = document.Animations[animIndex];
            var channels = new List<BlendAnimationChannelManifest>(animation.Channels.Count);
            for (var channelIndex = 0; channelIndex < animation.Channels.Count; channelIndex++)
            {
                var channel = animation.Channels[channelIndex];
                var timesPath = $"buffers/anim_{animIndex:D4}_ch_{channelIndex:D4}.times.bin";
                var valuesPath = $"buffers/anim_{animIndex:D4}_ch_{channelIndex:D4}.values.bin";
                WriteFloatBuffer(archive, timesPath, channel.Times);
                WriteFloatBuffer(archive, valuesPath, channel.Values);
                channels.Add(new BlendAnimationChannelManifest
                {
                    SkeletonIndex = channel.SkeletonIndex,
                    BoneIndex = channel.BoneIndex,
                    Property = channel.Property.ToString(),
                    TimesBuffer = timesPath,
                    ValuesBuffer = valuesPath,
                    KeyCount = channel.KeyCount,
                    ValueStride = channel.ValueStride,
                    Interpolation = channel.Interpolation.ToString()
                });
            }

            animations.Add(new BlendAnimationManifest { Name = animation.Name, Channels = channels });
        }

        return animations;
    }

    private static void WriteFloatBuffer(ZipArchive archive, string path, IReadOnlyList<float> values)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new BinaryWriter(stream);
        foreach (var value in values)
            writer.Write(value);
    }

    private static void WriteSkinBuffer(ZipArchive archive, string path, IReadOnlyList<ModelBoneInfluences> influences)
    {
        // Per-vertex layout: 4×int32 (joints) + 4×float32 (weights) = 32 bytes.
        // Parallels the vertex buffer index-for-index.
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new BinaryWriter(stream);
        foreach (var influence in influences)
        {
            writer.Write(influence.Joint0);
            writer.Write(influence.Joint1);
            writer.Write(influence.Joint2);
            writer.Write(influence.Joint3);
            writer.Write(influence.Weight0);
            writer.Write(influence.Weight1);
            writer.Write(influence.Weight2);
            writer.Write(influence.Weight3);
        }
    }

    private static void WriteTextureWibbleBuffer(
        ZipArchive archive,
        string path,
        IReadOnlyList<ModelVertex> vertices)
    {
        // Parallel-to-vertex layout, 10 x float32:
        // uVel, vVel, frequency, enabled, uAmp, uPhase, vAmp, vPhase, width, height.
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        using var writer = new BinaryWriter(stream);
        foreach (var vertex in vertices)
        {
            if (vertex.TextureWibble is { } wibble)
            {
                writer.Write((float)wibble.UVelocity);
                writer.Write((float)wibble.VVelocity);
                writer.Write((float)wibble.Frequency);
                writer.Write(1f);
                writer.Write((float)wibble.UAmplitude);
                writer.Write((float)wibble.UPhase);
                writer.Write((float)wibble.VAmplitude);
                writer.Write((float)wibble.VPhase);
                writer.Write((float)wibble.TextureWidth);
                writer.Write((float)wibble.TextureHeight);
            }
            else
            {
                for (var i = 0; i < 8; i++)
                    writer.Write(0f);
                writer.Write(1f);
                writer.Write(1f);
            }
        }
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
        if (string.IsNullOrWhiteSpace(sanitized))
            return "texture";

        return sanitized.Length <= 80
            ? sanitized
            : sanitized[..80].TrimEnd('_', '.', ' ');
    }
}
