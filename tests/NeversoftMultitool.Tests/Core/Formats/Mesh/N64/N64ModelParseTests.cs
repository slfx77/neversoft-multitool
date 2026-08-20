using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins the N64 model bundle path through the shared mesh pipeline: a
///     bundle read straight out of a .z64 combines the shell skeleton with its
///     decoded group2 render geometry. Embedded direct/compressed animation is
///     explicit opt-in so ordinary conversion remains a compact, unskinned
///     static model.
/// </summary>
public sealed class N64ModelParseTests(TestPaths paths)
{
    private const string Thps2N64Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2RomName = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string SpiderN64Build = "Spider-Man (2000-11-21, N64 - Final)";
    private const string SpiderRomName = "Spider-Man (USA).z64";

    private ModelDocument ParseBundle(
        string slot,
        out IArchiveFileSystem fs,
        IReadOnlyList<int>? animationIndices = null,
        bool includeAllAnimations = false)
    {
        return ParseBundle(
            Thps2N64Build, Thps2RomName, slot, out fs,
            animationIndices, includeAllAnimations);
    }

    private ModelDocument ParseBundle(
        string build,
        string rom,
        string slot,
        out IArchiveFileSystem fs,
        IReadOnlyList<int>? animationIndices = null,
        bool includeAllAnimations = false)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        fs = backend!.FileSystem;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "models_000",
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = animationIndices,
            IncludeAllN64Animations = includeAllAnimations
        });
    }

    [CorpusFact]
    public void SelectedDirectClip_BuildsSkinnedAnimatedGlbWithoutMovingBindGeometry()
    {
        var staticDocument = ParseBundle(
            SpiderN64Build, SpiderRomName, "002", out var staticFs);
        using var _ = staticFs;
        var animatedDocument = ParseBundle(
            SpiderN64Build, SpiderRomName, "002", out var animatedFs, [0]);
        using var __ = animatedFs;

        Assert.True(staticDocument.TriangleCount > 0);
        Assert.Equal(staticDocument.TriangleCount, animatedDocument.TriangleCount);
        Assert.Empty(staticDocument.Animations);
        Assert.All(staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));

        var native = Assert.IsType<N64ModelNativeSource>(animatedDocument.NativeSource);
        Assert.Equal(16, native.Shell.Objects.Count);
        var bank = N64CompressedAnimationBank.TryParse(native.ShellData);
        Assert.NotNull(bank);
        Assert.Equal(PsxMeshFile.HierChunkV1Tag, bank!.ChunkTag);
        Assert.Equal(3, bank.Entries.Count);

        var animation = Assert.Single(animatedDocument.Animations);
        Assert.Equal("anim_0", animation.Name);
        Assert.NotEmpty(animation.Channels);
        Assert.All(animatedDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
        AssertBindPoseLeavesVerticesUnchanged(animatedDocument);

        var (glb, triangles) = ModelExportService.BuildGlbBytes(animatedDocument);
        Assert.NotNull(glb);
        Assert.Equal(animatedDocument.TriangleCount, triangles);
        AssertKhronosClean(glb);
        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Single(model.LogicalAnimations);
        Assert.NotEmpty(model.LogicalSkins);
        Assert.All(model.LogicalMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
        });
    }

    private static void AssertKhronosClean(byte[] glb)
    {
        var validator = FindKhronosValidator();
        if (validator == null)
            return;

        var path = Path.Combine(
            Path.GetTempPath(), "nmt-n64-animation-" + Guid.NewGuid().ToString("N") + ".glb");
        try
        {
            File.WriteAllBytes(path, glb);
            var startInfo = new ProcessStartInfo
            {
                FileName = validator,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("--stdout");
            startInfo.ArgumentList.Add(path);
            using var process = Process.Start(startInfo);
            Assert.NotNull(process);
            var stdout = process!.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();
            Assert.True(process.WaitForExit(30_000), "Khronos glTF Validator timed out");
            Assert.True(process.ExitCode == 0,
                $"Khronos glTF Validator exit {process.ExitCode}:{Environment.NewLine}{stderr}{stdout}");
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private static string? FindKhronosValidator()
    {
        var directory = AppContext.BaseDirectory;
        for (var depth = 0; depth < 8 && directory != null; depth++)
        {
            var candidate = Path.Combine(
                directory, "tools", "vendor", "gltf-validator", "gltf_validator.exe");
            if (File.Exists(candidate))
                return candidate;
            directory = Path.GetDirectoryName(directory);
        }

        return null;
    }

    [CorpusFact]
    public void Parse_ProducesTheShellSkeletonAndRenderBankMetadata()
    {
        var document = ParseBundle("000", out var fs);
        using var _ = fs;

        Assert.Equal(ModelSourceKind.N64Model, document.SourceKind);

        var skeleton = Assert.Single(document.Skeletons);
        Assert.Equal(19, skeleton.Bones.Count);

        var metadata = Assert.Single(document.NativeMetadata.OfType<N64ModelRenderMetadata>());
        Assert.Equal(22u, metadata.RenderBankId);
        Assert.Equal(19, metadata.ObjectCount);
        Assert.True(metadata.RenderBankBytes > 0, "the render bank record should have loaded");
        Assert.True(metadata.GeometryDecoded);

        // Geometry from group2/022.bin: 570 triangles, split into one node per
        // G_MTX index so the character's parts stay separable.
        Assert.Equal(570, document.TriangleCount);
        Assert.NotEmpty(document.Meshes);
        var indices = document.Meshes
            .SelectMany(static m => m.Primitives)
            .Sum(static p => p.Indices.Length);
        Assert.Equal(570 * 3, indices);
        Assert.Equal(document.Meshes.Count, document.Nodes.Count(static n => n.MeshIndex.HasValue));
        Assert.Empty(document.Animations);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));
    }

    [CorpusFact]
    public void SelectedCompressedClip_AddsRigidGlobalInfluencesWithoutMovingBindGeometry()
    {
        var staticDocument = ParseBundle("045", out var staticFs);
        using var _ = staticFs;
        var animatedDocument = ParseBundle("045", out var animatedFs, [0]);
        using var __ = animatedFs;

        Assert.Equal(570, staticDocument.TriangleCount);
        Assert.Equal(staticDocument.TriangleCount, animatedDocument.TriangleCount);
        var animation = Assert.Single(animatedDocument.Animations);
        Assert.Equal("anim_0", animation.Name);
        Assert.NotEmpty(animation.Channels);
        AssertAnimationUsesShellTranslationScale(animatedDocument, animation);

        var staticPrimitives = staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives).ToArray();
        var animatedPrimitives = animatedDocument.Meshes.SelectMany(static mesh => mesh.Primitives).ToArray();
        Assert.Equal(staticPrimitives.Length, animatedPrimitives.Length);
        for (var primitiveIndex = 0; primitiveIndex < staticPrimitives.Length; primitiveIndex++)
        {
            var expected = staticPrimitives[primitiveIndex];
            var actual = animatedPrimitives[primitiveIndex];
            Assert.Equal(expected.Indices, actual.Indices);
            Assert.Equal(
                expected.Vertices.Select(static vertex => vertex.Position),
                actual.Vertices.Select(static vertex => vertex.Position));

            var skin = Assert.IsType<ModelSkinBinding>(actual.Skin);
            Assert.Equal(actual.Vertices.Length, skin.Influences.Length);
            Assert.All(skin.Influences, influence =>
            {
                Assert.InRange(influence.Joint0, 0, animatedDocument.Skeletons[0].Bones.Count - 1);
                Assert.Equal(1f, influence.Weight0);
                Assert.Equal(0f, influence.Weight1);
                Assert.Equal(0f, influence.Weight2);
                Assert.Equal(0f, influence.Weight3);
            });
        }

        // The render-bank corner position already includes object 0 plus the
        // G_MTX joint's world bind offset. Pin the complete rest-skin equation
        // so adding a rigid influence cannot apply that offset a second time:
        // position * inverseBind(joint) * worldBind(joint) == position.
        AssertBindPoseLeavesVerticesUnchanged(animatedDocument);

        var (glb, triangles) = ModelExportService.BuildGlbBytes(animatedDocument);
        Assert.NotNull(glb);
        Assert.Equal(570, triangles);
        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Single(model.LogicalAnimations);
        Assert.NotEmpty(model.LogicalSkins);
        Assert.All(model.LogicalMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
        });
    }

    [CorpusTheory]
    [InlineData(Thps2N64Build, Thps2RomName, "046", 0x2A, 110, 1, 33, true)]
    [InlineData(SpiderN64Build, SpiderRomName, "225", 0x2C, 16, 6, 1, true)]
    [InlineData(SpiderN64Build, SpiderRomName, "007", 0x2C, 46, 43, 1, false)]
    public void SelectedResidualClip_RoutesGlobalBindingThroughGlbAndRestPose(
        string build,
        string rom,
        string slot,
        int expectedTag,
        int expectedJointCount,
        int expectedClipCount,
        int expectedPlacementCount,
        bool expectRelativeLookupFailure)
    {
        var staticDocument = ParseBundle(build, rom, slot, out var staticFs);
        using var staticFileSystem = staticFs;
        var animatedDocument = ParseBundle(build, rom, slot, out var animatedFs, [0]);
        using var __ = animatedFs;

        Assert.Empty(staticDocument.Animations);
        Assert.All(staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));
        Assert.True(staticDocument.TriangleCount > 0);
        Assert.True(animatedDocument.TriangleCount > 0);

        var native = Assert.IsType<N64ModelNativeSource>(animatedDocument.NativeSource);
        Assert.Equal(expectedJointCount, native.Shell.Objects.Count);
        var bank = N64CompressedAnimationBank.TryParse(native.ShellData);
        Assert.NotNull(bank);
        Assert.Equal((uint)expectedTag, bank!.ChunkTag);
        Assert.Equal(expectedClipCount, bank.Entries.Count);

        var meshes = N64RenderBankFile.Parse(Assert.IsType<byte[]>(native.RenderBank));
        var byNode = meshes.ToDictionary(static mesh => mesh.NodeIndex);
        var placements = native.Shell.Objects.Select((obj, index) => (Object: obj, Index: index))
            .Where(item => byNode.TryGetValue(item.Object.MeshIndex, out var mesh)
                           && mesh.Triangles.Count > 0)
            .Select(item => (item.Index, Mesh: byNode[item.Object.MeshIndex]))
            .ToArray();
        Assert.Equal(expectedPlacementCount, placements.Length);
        Assert.True(N64AnimatedModelGate.TryCreateBindingPlan(
            native.Shell, meshes, out var binding));
        Assert.Equal(N64GeometryBindingMode.AnimatedGlobal, binding.Mode);
        var rigid = N64GeometryBindingPlan.StaticRelative(
            native.Shell.Objects.Count, 8f);
        var corners = placements.SelectMany(placement => placement.Mesh.Triangles
                .SelectMany(triangle => new[]
                {
                    (placement.Index, triangle.C0.MatrixIndex),
                    (placement.Index, triangle.C1.MatrixIndex),
                    (placement.Index, triangle.C2.MatrixIndex)
                }))
            .ToArray();
        if (expectRelativeLookupFailure)
        {
            Assert.Contains(corners, corner => !rigid.TryResolveOffsetObjectIndex(
                corner.Index, corner.MatrixIndex, out _));
        }
        else
        {
            Assert.True(native.Shell.HasHierarchy);
            Assert.DoesNotContain(corners, corner => !rigid.TryResolveOffsetObjectIndex(
                corner.Index, corner.MatrixIndex, out _));
            Assert.Contains(corners, corner =>
                rigid.ResolveOffsetObjectIndexOrDefault(corner.Index, corner.MatrixIndex)
                != binding.ResolveOffsetObjectIndexOrDefault(corner.Index, corner.MatrixIndex));
        }

        var animation = Assert.Single(animatedDocument.Animations);
        Assert.Equal("anim_0", animation.Name);
        Assert.NotEmpty(animation.Channels);
        var timedChannel = Assert.Single(animation.Channels.Where(static channel => channel.Times.Length > 1)
            .Take(1));
        Assert.Equal(1f / PsxAnimationBank.DefaultPreviewFps,
            timedChannel.Times[1] - timedChannel.Times[0], 5);
        Assert.All(animatedDocument.Meshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            var skin = Assert.IsType<ModelSkinBinding>(primitive.Skin);
            Assert.All(skin.Influences, influence =>
                Assert.InRange(influence.Joint0, 0, expectedJointCount - 1));
        });

        var staticPositions = staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.Position)
            .ToArray();
        var animatedPositions = animatedDocument.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.Position)
            .ToArray();
        Assert.False(staticPositions.SequenceEqual(animatedPositions),
            "successful global binding should differ from the legacy relative placement geometry");
        AssertBindPoseLeavesVerticesUnchanged(animatedDocument);

        var (glb, triangles) = ModelExportService.BuildGlbBytes(animatedDocument);
        Assert.NotNull(glb);
        Assert.Equal(animatedDocument.TriangleCount, triangles);
        AssertKhronosClean(glb);
        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Single(model.LogicalAnimations);
        Assert.NotEmpty(model.LogicalSkins);
        Assert.All(model.LogicalMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
        });
    }

    [CorpusFact]
    public void SelectedSpiderMapClip_UsesRelativeJointsK1GeometryAndCleanRestPose()
    {
        var staticDocument = ParseBundle(
            SpiderN64Build, SpiderRomName, "108", out var staticFs);
        using var staticFileSystem = staticFs;
        var animatedDocument = ParseBundle(
            SpiderN64Build, SpiderRomName, "108", out var animatedFs, [0]);
        using var animatedFileSystem = animatedFs;
        var rejectedDocument = ParseBundle(
            SpiderN64Build, SpiderRomName, "108", out var rejectedFs, [int.MaxValue]);
        using var rejectedFileSystem = rejectedFs;

        Assert.Empty(staticDocument.Animations);
        Assert.All(staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));
        Assert.Empty(rejectedDocument.Animations);
        Assert.All(rejectedDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));
        var animation = Assert.Single(animatedDocument.Animations);
        Assert.Equal("anim_0", animation.Name);
        Assert.NotEmpty(animation.Channels);
        Assert.Equal(staticDocument.TriangleCount, animatedDocument.TriangleCount);
        Assert.True(animatedDocument.TriangleCount > 0);

        var native = Assert.IsType<N64ModelNativeSource>(animatedDocument.NativeSource);
        var renderData = Assert.IsType<byte[]>(native.RenderBank);
        var meshes = N64RenderBankFile.Parse(renderData);
        var staticBinding = N64AnimatedModelGate.CreateStaticBindingPlan(
            native.ShellData,
            native.Shell,
            renderData,
            native.RenderBankId,
            meshes);
        var animatedPlan = N64AnimatedModelGate.TryOpen(
            native.ShellData,
            native.Shell,
            renderData,
            native.RenderBankId,
            meshes);
        Assert.NotNull(animatedPlan);
        Assert.Equal(N64GeometryBindingMode.StaticRelative, staticBinding.Mode);
        Assert.Equal(N64GeometryBindingMode.AnimatedRelative, animatedPlan!.Geometry.Mode);
        Assert.Equal(1f, staticBinding.VertexScaleFactor);
        Assert.Equal(1f, animatedPlan.Geometry.VertexScaleFactor);

        var staticPositions = staticDocument.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.Position)
            .ToArray();
        var animatedPositions = animatedDocument.Meshes.SelectMany(static mesh => mesh.Primitives)
            .SelectMany(static primitive => primitive.Vertices)
            .Select(static vertex => vertex.Position)
            .ToArray();
        Assert.Equal(staticPositions, animatedPositions);

        var (staticGlb, staticTriangles) = ModelExportService.BuildGlbBytes(staticDocument);
        var (rejectedGlb, rejectedTriangles) = ModelExportService.BuildGlbBytes(rejectedDocument);
        Assert.Equal(staticTriangles, rejectedTriangles);
        Assert.Equal(staticGlb, rejectedGlb);

        var byNode = meshes.ToDictionary(static mesh => mesh.NodeIndex);
        for (var objectIndex = 0; objectIndex < native.Shell.Objects.Count; objectIndex++)
        {
            var prefix = $"n64_{objectIndex:D4}_";
            var emittedMeshes = animatedDocument.Meshes
                .Where(mesh => mesh.Name.StartsWith(prefix, StringComparison.Ordinal))
                .ToArray();
            Assert.NotEmpty(emittedMeshes);
            Assert.All(emittedMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
            {
                var skin = Assert.IsType<ModelSkinBinding>(primitive.Skin);
                Assert.All(skin.Influences, influence =>
                    Assert.Equal(objectIndex, influence.Joint0));
            });

            var renderMesh = byNode[native.Shell.Objects[objectIndex].MeshIndex];
            var opaqueCorners = renderMesh.Triangles
                .Where(static triangle => !PsxFaceFlags.IsInvisible(triangle.FaceFlags)
                                          && (triangle.FaceFlags & PsxFaceFlags.SemiTransparent) == 0)
                .SelectMany(static triangle => new[]
                {
                    triangle.C0,
                    triangle.C1,
                    triangle.C2
                })
                .ToArray();
            if (opaqueCorners.Length == 0)
                continue;

            var corner = opaqueCorners.MaxBy(candidate =>
            {
                var vertex = renderMesh.Vertices[candidate.Vertex];
                return (long)vertex.X * vertex.X
                       + (long)vertex.Y * vertex.Y
                       + (long)vertex.Z * vertex.Z;
            });
            var raw = renderMesh.Vertices[corner.Vertex];
            var offset = PsxMeshSemantics.GetObjectOffset(
                native.Shell, native.Shell.Objects[objectIndex]);
            var k1Scale = 1f / native.Shell.ScaleDivisor;
            var expectedK1 = PsxMeshSemantics.ToGltfPosition(
                new Vector3(raw.X, raw.Y, raw.Z) * k1Scale + offset);
            var wrongK8 = PsxMeshSemantics.ToGltfPosition(
                new Vector3(raw.X, raw.Y, raw.Z) * (8f / native.Shell.ScaleDivisor) + offset);
            var emittedPositions = emittedMeshes.SelectMany(static mesh => mesh.Primitives)
                .SelectMany(static primitive => primitive.Vertices)
                .Select(static vertex => vertex.Position)
                .ToArray();
            Assert.Contains(emittedPositions,
                position => Vector3.Distance(position, expectedK1) < 1e-4f);
            if (Vector3.Distance(expectedK1, wrongK8) > 1e-3f)
            {
                Assert.DoesNotContain(emittedPositions,
                    position => Vector3.Distance(position, wrongK8) < 1e-4f);
            }
        }

        AssertBindPoseLeavesVerticesUnchanged(animatedDocument);
        var (glb, triangles) = ModelExportService.BuildGlbBytes(animatedDocument);
        Assert.NotNull(glb);
        Assert.Equal(animatedDocument.TriangleCount, triangles);
        AssertKhronosClean(glb);
        using var stream = new MemoryStream(glb, writable: false);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Single(model.LogicalAnimations);
        Assert.NotEmpty(model.LogicalSkins);
        Assert.All(model.LogicalMeshes.SelectMany(static mesh => mesh.Primitives), primitive =>
        {
            Assert.NotNull(primitive.GetVertexAccessor("JOINTS_0"));
            Assert.NotNull(primitive.GetVertexAccessor("WEIGHTS_0"));
        });
    }

    private static void AssertBindPoseLeavesVerticesUnchanged(ModelDocument document)
    {
        var skeleton = Assert.Single(document.Skeletons);
        var worldBinds = new Matrix4x4?[skeleton.Bones.Count];

        Matrix4x4 ResolveWorldBind(int index)
        {
            if (worldBinds[index] is { } resolved)
                return resolved;

            var bone = skeleton.Bones[index];
            resolved = bone.ParentIndex >= 0
                ? bone.LocalTransform * ResolveWorldBind(bone.ParentIndex)
                : bone.LocalTransform * skeleton.RootTransform;
            worldBinds[index] = resolved;
            return resolved;
        }

        // N64 HIER parents are not guaranteed to precede children in object
        // order, so resolve recursively instead of reading an uninitialized
        // parent matrix from a single forward pass.
        for (var i = 0; i < skeleton.Bones.Count; i++)
            _ = ResolveWorldBind(i);

        foreach (var primitive in document.Meshes.SelectMany(static mesh => mesh.Primitives))
        {
            var skin = Assert.IsType<ModelSkinBinding>(primitive.Skin);
            for (var i = 0; i < primitive.Vertices.Length; i++)
            {
                var position = primitive.Vertices[i].Position;
                var joint = skin.Influences[i].Joint0;
                var restSkin = skeleton.Bones[joint].InverseBindMatrix * worldBinds[joint]!.Value;
                var transformed = Vector3.Transform(position, restSkin);
                Assert.True(
                    Vector3.Distance(position, transformed) < 1e-5f,
                    $"joint {joint} moved bind vertex {i}: {position} -> {transformed}");
            }
        }
    }

    private static void AssertAnimationUsesShellTranslationScale(
        ModelDocument document,
        ModelAnimation modelAnimation)
    {
        var native = Assert.IsType<N64ModelNativeSource>(document.NativeSource);
        var bank = N64CompressedAnimationBank.TryParse(native.ShellData);
        Assert.NotNull(bank);
        var decoded = bank.DecodeSlot(0, native.Shell.Objects.Count);

        ModelAnimationChannel? selectedChannel = null;
        var selectedFrame = -1;
        foreach (var channel in modelAnimation.Channels.Where(static channel =>
                     channel.Property == ModelAnimationProperty.Translation))
        {
            var frame0 = decoded.GetBoneTranslation(channel.BoneIndex, 0);
            for (var frame = 1; frame < decoded.FrameCount; frame++)
            {
                if (decoded.GetBoneTranslation(channel.BoneIndex, frame) == frame0)
                    continue;

                selectedChannel = channel;
                selectedFrame = frame;
                break;
            }

            if (selectedChannel != null)
                break;
        }

        Assert.NotNull(selectedChannel);
        var boneIndex = selectedChannel.BoneIndex;
        var rawTranslation = decoded.GetBoneTranslation(boneIndex, selectedFrame);
        var expected = PsxMeshSemantics.ToGltfPosition(
            rawTranslation / native.Shell.ScaleDivisor);
        var wrongVertexScale = PsxMeshSemantics.ToGltfPosition(
            rawTranslation * (8f / native.Shell.ScaleDivisor));
        var offset = selectedFrame * 3;
        var actual = new Vector3(
            selectedChannel.Values[offset],
            selectedChannel.Values[offset + 1],
            selectedChannel.Values[offset + 2]);

        Assert.True(Vector3.Distance(expected, actual) < 1e-5f,
            $"N64 translation used a divisor other than shell ScaleDivisor {native.Shell.ScaleDivisor}: "
            + $"bone {boneIndex}, frame {selectedFrame}, raw {rawTranslation}, "
            + $"expected {expected}, actual {actual}, x8 {wrongVertexScale}");
        Assert.True(Vector3.Distance(wrongVertexScale, actual) > 1e-3f,
            "N64 animation translation incorrectly inherited the render-vertex x8 scale");
    }

    [CorpusFact]
    public void InvalidExactSelection_FailsBackToByteIdenticalStaticGlb()
    {
        // slot 046 is the 33-placement direct-matrix sk2def control. An
        // invalid opt-in must not activate its otherwise valid global plan.
        var staticDocument = ParseBundle("046", out var staticFs);
        using var _ = staticFs;
        var rejectedDocument = ParseBundle("046", out var rejectedFs, [int.MaxValue]);
        using var __ = rejectedFs;

        Assert.Empty(rejectedDocument.Animations);
        Assert.All(rejectedDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));

        var (staticGlb, staticTriangles) = ModelExportService.BuildGlbBytes(staticDocument);
        var (rejectedGlb, rejectedTriangles) = ModelExportService.BuildGlbBytes(rejectedDocument);
        Assert.Equal(staticTriangles, rejectedTriangles);
        Assert.Equal(staticGlb, rejectedGlb);
    }

    [CorpusFact]
    public void AllSelectedDecodesFail_PreservesUnskinnedByteIdenticalStaticGlb()
    {
        // Spider-Man slot 225 is a newly admitted compressed shell whose
        // non-zero placement makes relative G_MTX addressing go out of range.
        var romPath = paths.FindSampleFile(SpiderN64Build, SpiderRomName);
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fileSystem = backend.FileSystem;
        var entry = N64Bundles.FindBundle(backend, "225");
        var archiveSource = new ArchiveAssetSource(backend, entry);
        var corruptSource = new OverrideBytesAssetSource(
            archiveSource,
            MakeFirstClipOwnOneByte(archiveSource.ReadBytes()));

        var parser = new MeshModelParser();
        ModelDocument Parse(IReadOnlyList<int>? animationIndices) => parser.Parse(new MeshImportRequest
        {
            Source = corruptSource,
            FileName = entry.Name,
            OutputStem = "models_225",
            SourceKind = ModelSourceKind.N64Model,
            N64AnimationIndices = animationIndices
        });

        var staticDocument = Parse(null);
        var rejectedDocument = Parse([0]);

        Assert.Empty(rejectedDocument.Animations);
        Assert.All(rejectedDocument.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.Null(primitive.Skin));
        var (staticGlb, staticTriangles) = ModelExportService.BuildGlbBytes(staticDocument);
        var (rejectedGlb, rejectedTriangles) = ModelExportService.BuildGlbBytes(rejectedDocument);
        Assert.True(staticTriangles > 0);
        Assert.Equal(staticTriangles, rejectedTriangles);
        Assert.Equal(staticGlb, rejectedGlb);
    }

    [CorpusFact]
    public void AllClipOptIn_ExportsTheWholeRealBank()
    {
        var document = ParseBundle("045", out var fs, includeAllAnimations: true);
        using var _ = fs;

        Assert.Equal(218, document.Animations.Count);
        Assert.All(document.Meshes.SelectMany(static mesh => mesh.Primitives),
            static primitive => Assert.NotNull(primitive.Skin));
    }

    /// <summary>
    ///     End-to-end: a bundle read from the ROM exports a real GLB carrying
    ///     the decoded render-bank geometry.
    /// </summary>
    [CorpusFact]
    public void Document_ExportsAValidGlbWithGeometry()
    {
        var document = ParseBundle("000", out var fs);
        using var _ = fs;

        var (glb, triangles) = ModelExportService.BuildGlbBytes(document);

        Assert.NotNull(glb);
        Assert.Equal(570, triangles);
        // glTF binary container magic.
        Assert.Equal((byte)'g', glb![0]);
        Assert.Equal((byte)'l', glb[1]);
        Assert.Equal((byte)'T', glb[2]);
        Assert.Equal((byte)'F', glb[3]);
        Assert.True(glb.Length > 512, $"expected a populated GLB, got {glb.Length} bytes");
    }

    [CorpusFact]
    public void AuthoredTextureWrap_ReachesTheExportedGltfSampler()
    {
        // The dictionary record's cmS/cmT bytes were unread until 2026-08-20,
        // so every N64 material exported REPEAT regardless of what the art
        // authored. Roughly a third to a half of each ROM's records clamp
        // (pinned per ROM by N64TexFileTests), which is why the omission bled
        // edge texels rather than being cosmetic.
        var romPath = paths.FindSampleFile(SpiderN64Build, SpiderRomName);
        Assert.SkipWhen(romPath == null, "Spider-Man N64 ROM sample not available");
        var backend = ArchiveAssetBackend.TryOpen(romPath!);
        Assert.NotNull(backend);
        using var fs = backend.FileSystem;

        // Scan rather than name one slot: which bundle happens to carry a
        // clamped texture is incidental, but that BOTH modes reach the sampler
        // somewhere in a ROM is the property under test.
        var seen = new HashSet<(ModelTextureWrap U, ModelTextureWrap V)>();
        byte[]? clampedGlb = null;
        foreach (var entry in fs.Entries.Where(static e =>
                     e.Name.EndsWith(".psx.n64", StringComparison.OrdinalIgnoreCase)))
        {
            ModelDocument document;
            try
            {
                document = new MeshModelParser().Parse(new MeshImportRequest
                {
                    Source = new ArchiveAssetSource(backend, entry),
                    FileName = entry.Name,
                    OutputStem = "wrap_probe",
                    SourceKind = ModelSourceKind.N64Model
                });
            }
            catch (InvalidOperationException)
            {
                continue; // Authored-empty shell.
            }

            foreach (var texture in document.Textures)
                seen.Add((texture.WrapU, texture.WrapV));

            if (clampedGlb == null && document.Textures.Any(static t =>
                    t.WrapU == ModelTextureWrap.ClampToEdge
                    || t.WrapV == ModelTextureWrap.ClampToEdge))
                clampedGlb = ModelExportService.BuildGlbBytes(document).GlbBytes;

            if (clampedGlb != null && seen.Count > 1) break;
        }

        // Uniformly REPEAT would mean the sampler is not carrying authored
        // state at all — which is exactly the pre-2026-08-20 behaviour.
        Assert.Contains((ModelTextureWrap.Repeat, ModelTextureWrap.Repeat), seen);
        Assert.Contains(seen, w =>
            w.U == ModelTextureWrap.ClampToEdge || w.V == ModelTextureWrap.ClampToEdge);

        Assert.NotNull(clampedGlb);
        using var stream = new MemoryStream(clampedGlb!);
        var model = ModelRoot.ReadGLB(stream);
        Assert.Contains(
            model.LogicalTextures,
            static t => t.Sampler?.WrapS == TextureWrapMode.CLAMP_TO_EDGE
                        || t.Sampler?.WrapT == TextureWrapMode.CLAMP_TO_EDGE);
    }

    [CorpusFact]
    public void Parse_RejectsAnEmptyBundleSlotWithAClearMessage()
    {
        // models/049 is a 24-byte authored-empty shell.
        var error = Assert.Throws<InvalidOperationException>(() =>
        {
            var document = ParseBundle("049", out var fs);
            fs.Dispose();
            return document;
        });

        Assert.Contains("N64 model shell", error.Message, StringComparison.Ordinal);
    }

    private static byte[] MakeFirstClipOwnOneByte(byte[] source)
    {
        var bytes = (byte[])source.Clone();
        var cursor = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(4)));
        var compressedChunk = -1;
        while (cursor + 8 <= bytes.Length)
        {
            var tag = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor));
            if (tag == uint.MaxValue)
                break;

            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(cursor + 4)));
            var dataStart = cursor + 8;
            if (tag == PsxMeshFile.HierChunkV2Tag)
                compressedChunk = dataStart;
            cursor = checked(dataStart + length);
        }

        Assert.True(compressedChunk >= 0, "real fixture should contain compressed 0x2C animation data");
        var count = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(compressedChunk));
        Assert.True(count >= 3, "fixture needs a following offset to preserve table monotonicity");
        var firstPoolOffset = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(compressedChunk + 4));
        BinaryPrimitives.WriteUInt32BigEndian(
            bytes.AsSpan(compressedChunk + 12),
            checked(firstPoolOffset + 1));

        var bank = N64CompressedAnimationBank.TryParse(bytes);
        Assert.NotNull(bank);
        var shell = PsxN64ShellFile.Parse(bytes);
        Assert.NotNull(shell);
        Assert.NotNull(Record.Exception(() => bank!.DecodeSlot(0, shell!.Objects.Count)));
        return bytes;
    }

    private sealed class OverrideBytesAssetSource(AssetSource inner, byte[] bytes) : AssetSource
    {
        public override string DisplayName => inner.DisplayName;
        public override string EntryName => inner.EntryName;
        public override string? FileSystemPath => inner.FileSystemPath;
        public override byte[] ReadBytes() => bytes;
        public override bool CompanionExists(string nameWithExtension) =>
            inner.CompanionExists(nameWithExtension);
        public override byte[]? TryReadCompanion(string nameWithExtension) =>
            inner.TryReadCompanion(nameWithExtension);
        public override byte[]? TryReadCompanion(
            string stem,
            IReadOnlyList<string> extensions,
            IReadOnlyList<string>? subdirs = null) =>
            inner.TryReadCompanion(stem, extensions, subdirs);
    }
}
