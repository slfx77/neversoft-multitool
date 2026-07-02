using System.IO.Compression;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class ModelExportServiceSkinningTests
{
    [Fact]
    public void Export_Glb_EmitsSkinJointsAndPerVertexInfluences()
    {
        using var temp = new TempDirectory();
        var document = CreateTwoBoneSkinnedDocument();

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal(2, result.Triangles);

        var (jsonBytes, _) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var root = gltf.RootElement;

        Assert.True(root.TryGetProperty("skins", out var skins));
        Assert.Equal(1, skins.GetArrayLength());
        var jointCount = skins[0].GetProperty("joints").GetArrayLength();
        Assert.Equal(2, jointCount);
        Assert.True(skins[0].TryGetProperty("inverseBindMatrices", out _),
            "Skin must reference an inverseBindMatrices accessor.");

        var primitive = root.GetProperty("meshes")[0].GetProperty("primitives")[0];
        var attributes = primitive.GetProperty("attributes");
        Assert.True(attributes.TryGetProperty("JOINTS_0", out _), "Missing JOINTS_0 vertex attribute.");
        Assert.True(attributes.TryGetProperty("WEIGHTS_0", out _), "Missing WEIGHTS_0 vertex attribute.");
    }

    [Fact]
    public void BlendManifest_EmitsSkeletonAndSkinInfluenceBuffer()
    {
        var document = CreateTwoBoneSkinnedDocument();
        using var ms = new MemoryStream();
        BlendPackageWriter.Write(document, ms, "synthetic.blend");
        ms.Position = 0;

        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);

        using var manifestStream = manifestEntry!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var skeletons = manifest.RootElement.GetProperty("Skeletons");
        Assert.Equal(1, skeletons.GetArrayLength());
        Assert.Equal(2, skeletons[0].GetProperty("Bones").GetArrayLength());

        var primitive = manifest.RootElement
            .GetProperty("Meshes")[0]
            .GetProperty("Primitives")[0];
        var skin = primitive.GetProperty("Skin");
        Assert.Equal(0, skin.GetProperty("SkeletonIndex").GetInt32());
        Assert.Equal(4, skin.GetProperty("InfluenceCount").GetInt32());
        var influenceBufferPath = skin.GetProperty("InfluenceBuffer").GetString();
        Assert.NotNull(influenceBufferPath);
        var influenceEntry = archive.GetEntry(influenceBufferPath!);
        Assert.NotNull(influenceEntry);
        // 4 vertices × (4×int32 + 4×float32) = 4 × 32 = 128 bytes.
        Assert.Equal(128, influenceEntry!.Length);
    }

    [Fact]
    public void Export_Glb_EmitsRotationAnimationChannel()
    {
        using var temp = new TempDirectory();
        var document = CreateTwoBoneSkinnedDocument();
        AddSyntheticRotationAnimation(document, "spin", skeletonIndex: 0, boneIndex: 1);

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest { OutputDirectory = temp.Path, Format = MeshOutputFormat.Glb });
        var outputPath = Assert.Single(result.OutputPaths);

        var (jsonBytes, _) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var root = gltf.RootElement;

        Assert.True(root.TryGetProperty("animations", out var animations));
        Assert.Equal(1, animations.GetArrayLength());
        var animation = animations[0];
        Assert.Equal("spin", animation.GetProperty("name").GetString());

        var channels = animation.GetProperty("channels");
        Assert.Equal(1, channels.GetArrayLength());
        var channel = channels[0];
        Assert.Equal("rotation", channel.GetProperty("target").GetProperty("path").GetString());

        var samplerIndex = channel.GetProperty("sampler").GetInt32();
        var sampler = animation.GetProperty("samplers")[samplerIndex];
        var inputAccessor = root.GetProperty("accessors")[sampler.GetProperty("input").GetInt32()];
        Assert.Equal(3, inputAccessor.GetProperty("count").GetInt32());
    }

    [Fact]
    public void BlendManifest_EmitsAnimationChannelsAndBuffers()
    {
        var document = CreateTwoBoneSkinnedDocument();
        AddSyntheticRotationAnimation(document, "spin", skeletonIndex: 0, boneIndex: 1);

        using var ms = new MemoryStream();
        BlendPackageWriter.Write(document, ms, "synthetic.blend");
        ms.Position = 0;
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json");
        Assert.NotNull(manifestEntry);

        using var manifestStream = manifestEntry!.Open();
        using var manifest = JsonDocument.Parse(manifestStream);
        var animations = manifest.RootElement.GetProperty("Animations");
        Assert.Equal(1, animations.GetArrayLength());
        Assert.Equal("spin", animations[0].GetProperty("Name").GetString());

        var channels = animations[0].GetProperty("Channels");
        Assert.Equal(1, channels.GetArrayLength());
        var channel = channels[0];
        Assert.Equal("Rotation", channel.GetProperty("Property").GetString());
        Assert.Equal(0, channel.GetProperty("SkeletonIndex").GetInt32());
        Assert.Equal(1, channel.GetProperty("BoneIndex").GetInt32());
        Assert.Equal(3, channel.GetProperty("KeyCount").GetInt32());
        Assert.Equal(4, channel.GetProperty("ValueStride").GetInt32());

        var timesEntry = archive.GetEntry(channel.GetProperty("TimesBuffer").GetString()!);
        Assert.NotNull(timesEntry);
        // 3 keys × float32 = 12 bytes.
        Assert.Equal(12, timesEntry!.Length);

        var valuesEntry = archive.GetEntry(channel.GetProperty("ValuesBuffer").GetString()!);
        Assert.NotNull(valuesEntry);
        // 3 keys × 4 floats = 48 bytes.
        Assert.Equal(48, valuesEntry!.Length);
    }

    [Fact]
    public void Export_Glb_SkeletonOnlyDocumentEmitsAnimationAndNoMeshes()
    {
        using var temp = new TempDirectory();
        var skeleton = CreateTwoBonePs2Skeleton();
        var animation = CreateSyntheticSkaRotationAnimation();

        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            skeleton, [("spin", animation)], "two_bone");

        var result = ModelExportService.Export(
            document,
            new MeshExportRequest
            {
                OutputDirectory = temp.Path,
                Format = MeshOutputFormat.Glb
            });

        var outputPath = Assert.Single(result.OutputPaths);
        Assert.Equal(0, result.Triangles);

        var (jsonBytes, _) = ReadGlbChunks(outputPath);
        using var gltf = JsonDocument.Parse(jsonBytes);
        var root = gltf.RootElement;

        Assert.False(root.TryGetProperty("meshes", out _),
            "Skeleton-only document must not emit any meshes.");
        Assert.True(root.TryGetProperty("nodes", out var nodes));
        Assert.True(nodes.GetArrayLength() >= 2,
            "Both bones must appear as nodes in the glTF.");

        Assert.True(root.TryGetProperty("animations", out var animations));
        var emitted = animations[0];
        Assert.Equal("spin", emitted.GetProperty("name").GetString());
        Assert.Single(emitted.GetProperty("channels").EnumerateArray());
    }

    [Fact]
    public void Ps2SceneParser_SkaAnimationsRequestPopulatesModelAnimations()
    {
        var skeleton = CreateTwoBonePs2Skeleton();
        var animation = CreateSyntheticSkaRotationAnimation();
        var document = SkaModelDocumentBuilder.BuildSkeletonOnly(
            skeleton, [("spin", animation)], "two_bone");

        // Mirror the bridge used by ParsePs2Scene: PopulateSkaAnimations on the
        // same skeleton-indexed document. The structural contract under test is
        // that channels carry the right key count and stride.
        var modelAnimation = Assert.Single(document.Animations);
        Assert.Equal("spin", modelAnimation.Name);

        var rotationChannel = Assert.Single(
            modelAnimation.Channels,
            c => c.Property == ModelAnimationProperty.Rotation);
        Assert.Equal(0, rotationChannel.SkeletonIndex);
        Assert.Equal(1, rotationChannel.BoneIndex);
        Assert.Equal(3, rotationChannel.KeyCount);
        Assert.Equal(4, rotationChannel.ValueStride);
        Assert.Equal(12, rotationChannel.Values.Length);
    }

    private static Ps2Skeleton CreateTwoBonePs2Skeleton()
    {
        return new Ps2Skeleton
        {
            Version = 2,
            Flags = 0,
            Bones =
            [
                new Ps2Bone
                {
                    NameChecksum = 0x1000,
                    ParentChecksum = 0,
                    FlipChecksum = 0,
                    ParentIndex = -1,
                    LocalRotation = Quaternion.Identity,
                    LocalTranslation = Vector3.Zero,
                    InverseBindMatrix = Matrix4x4.Identity
                },
                new Ps2Bone
                {
                    NameChecksum = 0x1001,
                    ParentChecksum = 0x1000,
                    FlipChecksum = 0,
                    ParentIndex = 0,
                    LocalRotation = Quaternion.Identity,
                    LocalTranslation = new Vector3(0f, 1f, 0f),
                    InverseBindMatrix = Matrix4x4.CreateTranslation(0f, -1f, 0f)
                }
            ]
        };
    }

    private static SkaAnimation CreateSyntheticSkaRotationAnimation()
    {
        var keys = new SkaRotationKey[3];
        for (var i = 0; i < 3; i++)
        {
            var angle = MathF.PI * i / 2f;
            keys[i] = new SkaRotationKey(i * 0.5f,
                Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle));
        }

        return new SkaAnimation
        {
            Version = 2,
            Flags = 0,
            Duration = 1f,
            BoneTracks =
            [
                new SkaBoneTrack
                {
                    BoneIndex = 0,
                    RotationKeys = [],
                    TranslationKeys = []
                },
                new SkaBoneTrack
                {
                    BoneIndex = 1,
                    RotationKeys = keys,
                    TranslationKeys = []
                }
            ]
        };
    }

    private static void AddSyntheticRotationAnimation(
        ModelDocument document, string name, int skeletonIndex, int boneIndex)
    {
        // Three keyframes at t=0, 0.5, 1.0 rotating 0 -> 90 -> 180 deg about Y.
        var times = new[] { 0f, 0.5f, 1f };
        var values = new float[12];
        for (var i = 0; i < 3; i++)
        {
            var angle = MathF.PI * i / 2f;
            var q = Quaternion.CreateFromAxisAngle(Vector3.UnitY, angle);
            var o = i * 4;
            values[o] = q.X;
            values[o + 1] = q.Y;
            values[o + 2] = q.Z;
            values[o + 3] = q.W;
        }

        var animation = new ModelAnimation { Name = name };
        animation.Channels.Add(new ModelAnimationChannel
        {
            SkeletonIndex = skeletonIndex,
            BoneIndex = boneIndex,
            Property = ModelAnimationProperty.Rotation,
            Times = times,
            Values = values
        });
        document.Animations.Add(animation);
    }

    private static ModelDocument CreateTwoBoneSkinnedDocument()
    {
        var document = new ModelDocument { Name = "two_bone_skin" };
        document.Materials.Add(new RenderMaterial
        {
            Name = "skin_mat",
            BaseColor = Vector4.One
        });

        var skeleton = new ModelSkeleton { Name = "skeleton" };
        skeleton.Bones.Add(new ModelBone
        {
            Name = "root",
            ParentIndex = -1,
            LocalTransform = Matrix4x4.Identity,
            InverseBindMatrix = Matrix4x4.Identity
        });
        skeleton.Bones.Add(new ModelBone
        {
            Name = "child",
            ParentIndex = 0,
            LocalTransform = Matrix4x4.CreateTranslation(0f, 1f, 0f),
            InverseBindMatrix = Matrix4x4.CreateTranslation(0f, -1f, 0f)
        });
        document.Skeletons.Add(skeleton);

        // 4 vertices form 2 triangles (a quad). 50/50 weight to bones 0 and 1.
        var vertices = new[]
        {
            new ModelVertex(new Vector3(0f, 0f, 0f), Vector3.UnitZ, Vector4.One, Vector2.Zero),
            new ModelVertex(new Vector3(1f, 0f, 0f), Vector3.UnitZ, Vector4.One, Vector2.UnitX),
            new ModelVertex(new Vector3(1f, 1f, 0f), Vector3.UnitZ, Vector4.One, Vector2.One),
            new ModelVertex(new Vector3(0f, 1f, 0f), Vector3.UnitZ, Vector4.One, Vector2.UnitY)
        };
        var indices = new[] { 0, 1, 2, 0, 2, 3 };
        var influences = new[]
        {
            new ModelBoneInfluences(0, 1, 0, 0, 0.7f, 0.3f, 0f, 0f),
            new ModelBoneInfluences(0, 1, 0, 0, 0.6f, 0.4f, 0f, 0f),
            new ModelBoneInfluences(0, 1, 0, 0, 0.4f, 0.6f, 0f, 0f),
            new ModelBoneInfluences(0, 1, 0, 0, 0.3f, 0.7f, 0f, 0f)
        };

        var mesh = new ModelMesh { Name = "skinned_mesh" };
        mesh.Primitives.Add(new ModelPrimitive
        {
            Name = "skinned_primitive",
            MaterialIndex = 0,
            Vertices = vertices,
            Indices = indices,
            Skin = new ModelSkinBinding
            {
                SkeletonIndex = 0,
                Influences = influences
            }
        });
        document.Meshes.Add(mesh);
        document.Nodes.Add(new ModelNode
        {
            Name = "skin_node",
            MeshIndex = 0,
            Transform = Matrix4x4.Identity
        });
        document.Scenes.Add(new ModelScene { Name = "scene" });
        document.Scenes[0].RootNodeIndices.Add(0);

        return document;
    }

    private static (byte[] JsonBytes, byte[] BinBytes) ReadGlbChunks(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        reader.ReadUInt32();           // magic
        reader.ReadUInt32();           // version
        reader.ReadUInt32();           // length
        var jsonLen = reader.ReadUInt32();
        reader.ReadUInt32();           // chunk type (JSON)
        var jsonBytes = reader.ReadBytes((int)jsonLen);
        var binLen = reader.ReadUInt32();
        reader.ReadUInt32();           // chunk type (BIN)
        var binBytes = reader.ReadBytes((int)binLen);
        return (jsonBytes, binBytes);
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "NsMtSkinTests_" + Guid.NewGuid().ToString("N"));
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
