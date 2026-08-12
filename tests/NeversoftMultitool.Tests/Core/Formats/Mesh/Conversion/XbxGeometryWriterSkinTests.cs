using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.Mesh.XbxScene;
using SharpGLTF.Schema2;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.Mesh.XbxScene.XbxScene;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class XbxGeometryWriterSkinTests(TestPaths paths)
{
    private const string Thug2XboxBuild =
        "Tony Hawk's Underground 2 (2004-10-4, Xbox - Final)";
    private const string ThawPcBuild =
        "Tony Hawk's American Wasteland (2006-2-6, PC - Final)";
    private const string ThawGcBuild =
        "Tony Hawk's American Wasteland (2005-8-22, GC - Final)";

    [Theory]
    [InlineData("Xbox (THUG2)", true, true)]
    [InlineData("PC (THAW)", true, true)]
    [InlineData("GameCube (THAW)", true, true)]
    [InlineData("GameCube (THAW)", false, false)]
    [InlineData("PS2 (THAW)", true, false)]
    [InlineData(null, true, false)]
    public void GuiEligibility_RequiresRetainedSkinRecordsOnAnXbxFamilyScene(
        string? format,
        bool hasSkinnedSectors,
        bool expected)
    {
        Assert.Equal(expected, XbxSkeletonEligibility.Supports(format, hasSkinnedSectors));
    }

    [Fact]
    public void GuiControlState_HidesIneligibleAndDisablesAllChangesWhileOwned()
    {
        Assert.Equal(
            new XbxSkeletonControlState(false, false, false),
            XbxSkeletonControlState.Create(
                eligibleEntrySelected: false,
                skeletonSelected: true,
                operationActive: false));
        Assert.Equal(
            new XbxSkeletonControlState(true, true, false),
            XbxSkeletonControlState.Create(
                eligibleEntrySelected: true,
                skeletonSelected: false,
                operationActive: false));
        Assert.Equal(
            new XbxSkeletonControlState(true, true, true),
            XbxSkeletonControlState.Create(
                eligibleEntrySelected: true,
                skeletonSelected: true,
                operationActive: false));
        Assert.Equal(
            new XbxSkeletonControlState(true, false, false),
            XbxSkeletonControlState.Create(
                eligibleEntrySelected: true,
                skeletonSelected: true,
                operationActive: true));
    }

    [Fact]
    public void GuiCoordinateScale_AppliesOnlyToWorldzonesInMixedBatch()
    {
        Assert.Equal(2f, MeshGuiCoordinateScalePolicy.Resolve(
            isPakWorldzone: true, requestedScale: 2f));
        Assert.Equal(1f, MeshGuiCoordinateScalePolicy.Resolve(
            isPakWorldzone: false, requestedScale: 2f));
    }

    [Fact]
    public void GuiRenderPolicy_RebuildsEligibleExplicitSkeletonEntries()
    {
        Assert.False(MeshGuiRenderPolicy.IsSkeletonPreviewPending(
            hasPreviewEntry: false,
            isSameEntry: true,
            isSameSelection: true));
        Assert.True(MeshGuiRenderPolicy.IsSkeletonPreviewPending(
            hasPreviewEntry: true,
            isSameEntry: true,
            isSameSelection: true));
        Assert.False(MeshGuiRenderPolicy.RequiresEntryRebuild(
            isPakWorldzone: false,
            hasSupportedLevelObjectCompanion: false,
            supportsExplicitXbxSkeleton: false));
        Assert.True(MeshGuiRenderPolicy.RequiresEntryRebuild(
            isPakWorldzone: false,
            hasSupportedLevelObjectCompanion: false,
            supportsExplicitXbxSkeleton: true));
    }

    [Fact]
    public void RootOnlySkeleton_AllZeroPackedVerticesBindToRootDespiteHasSkinDataFalse()
    {
        var scene = CreateTriangleScene(
            CreateVertex(Vector3.Zero, hasSkinData: false),
            CreateVertex(Vector3.UnitX, hasSkinData: false),
            CreateVertex(Vector3.UnitY, hasSkinData: false));

        var document = BuildDocument(scene, BuildSkeleton(1));

        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        var skin = Assert.IsType<ModelSkinBinding>(primitive.Skin);
        Assert.Single(document.Skeletons);
        Assert.All(skin.Influences, influence =>
            Assert.Equal(ModelBoneInfluences.Single(0), influence));
    }

    [Fact]
    public void PositiveOutOfRangeJoint_FailsClosedToByteIdenticalRigidOutput()
    {
        var bad = CreateVertex(Vector3.Zero, true, joint0: 1, weight0: 1f);
        var scene = CreateTriangleScene(
            bad,
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false));

        AssertRigidByteParity(scene, BuildSkeleton(1), coordinateScale: 1f);
    }

    [Fact]
    public void ZeroWeightOutOfRangeJoint_DoesNotRejectValidPositiveInfluence()
    {
        var weighted = CreateVertex(
            Vector3.Zero,
            hasSkinData: true,
            joint0: 0,
            weight0: 2f,
            joint3: 999,
            weight3: 0f);
        var scene = CreateTriangleScene(
            weighted,
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false));

        var document = BuildDocument(scene, BuildSkeleton(1));

        var skin = Assert.IsType<ModelSkinBinding>(
            Assert.Single(Assert.Single(document.Meshes).Primitives).Skin);
        Assert.Equal(1f, skin.Influences[0].Weight0);
        Assert.Equal(0, skin.Influences[0].Joint3);
        Assert.Equal(0f, skin.Influences[0].Weight3);
    }

    [Fact]
    public void SubnormalPositiveWeight_NormalizesWithoutOverflow()
    {
        var weighted = CreateVertex(
            Vector3.Zero,
            hasSkinData: true,
            weight0: float.Epsilon);
        var scene = CreateTriangleScene(
            weighted,
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false));

        var document = BuildDocument(scene, BuildSkeleton(1));

        var skin = Assert.IsType<ModelSkinBinding>(
            Assert.Single(Assert.Single(document.Meshes).Primitives).Skin);
        Assert.True(float.IsFinite(skin.Influences[0].Weight0));
        Assert.Equal(1f, skin.Influences[0].Weight0);
    }

    [Fact]
    public void InvalidUnreferencedVertex_DoesNotRejectEmittedSkin()
    {
        var vertices = new[]
        {
            CreateVertex(Vector3.Zero, true, weight0: 1f),
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false),
            CreateVertex(Vector3.One, true, joint0: 99, weight0: 1f)
        };
        var scene = CreateScene(CreateSector(vertices, [0, 1, 2], isSkinned: true));

        var document = BuildDocument(scene, BuildSkeleton(1));

        Assert.Single(document.Skeletons);
        Assert.NotNull(Assert.Single(Assert.Single(document.Meshes).Primitives).Skin);
    }

    [Fact]
    public void InvalidDiscardedDegenerateTriangle_DoesNotBlockValidTriangle()
    {
        var vertices = new[]
        {
            CreateVertex(Vector3.Zero, true, weight0: 1f),
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false),
            CreateVertex(Vector3.One, true, joint0: 99, weight0: 1f)
        };
        var scene = CreateScene(CreateSector(
            vertices,
            [0, 1, 2, 0, 0, 3],
            isSkinned: true));

        var document = BuildDocument(scene, BuildSkeleton(1));

        Assert.Single(document.Skeletons);
        var primitive = Assert.Single(Assert.Single(document.Meshes).Primitives);
        Assert.NotNull(primitive.Skin);
        Assert.Equal(3, primitive.Vertices.Length);
    }

    [Fact]
    public void NonUnitCoordinateScale_PreservesByteIdenticalRigidOutput()
    {
        var scene = CreateTriangleScene(
            CreateVertex(Vector3.Zero, true, weight0: 1f),
            CreateVertex(Vector3.UnitX, true, weight0: 1f),
            CreateVertex(Vector3.UnitY, true, weight0: 1f));

        AssertRigidByteParity(scene, BuildSkeleton(1), coordinateScale: 2f);
    }

    [Fact]
    public void MalformedInfluence_FailsClosedWithoutOrphanSkeletonOrSkin()
    {
        var malformed = CreateVertex(Vector3.Zero, true, weight0: float.NaN);
        var scene = CreateTriangleScene(
            malformed,
            CreateVertex(Vector3.UnitX, false),
            CreateVertex(Vector3.UnitY, false));

        var document = BuildDocument(scene, BuildSkeleton(1));

        Assert.Empty(document.Skeletons);
        Assert.All(document.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));
    }

    [Fact]
    public void SectorFlagAndEmittedTriangleAreBothRequiredForSkinEmission()
    {
        var scene = CreateTriangleScene(
            CreateVertex(Vector3.Zero, true, weight0: 1f),
            CreateVertex(Vector3.UnitX, true, weight0: 1f),
            CreateVertex(Vector3.UnitY, true, weight0: 1f),
            isSkinnedSector: false);

        var document = BuildDocument(scene, BuildSkeleton(1));

        Assert.Empty(document.Skeletons);
        Assert.Null(Assert.Single(Assert.Single(document.Meshes).Primitives).Skin);
    }

    [Fact]
    public void MalformedUnskinnedSector_DoesNotBlockValidSkinnedSector()
    {
        var valid = CreateSector(
            [
                CreateVertex(Vector3.Zero, true, weight0: 1f),
                CreateVertex(Vector3.UnitX, false),
                CreateVertex(Vector3.UnitY, false)
            ],
            [0, 1, 2],
            isSkinned: true,
            checksum: 1);
        var malformedRigid = CreateSector(
            [
                CreateVertex(Vector3.Zero, true, joint0: 99, weight0: 1f),
                CreateVertex(Vector3.UnitX, false),
                CreateVertex(Vector3.UnitY, false)
            ],
            [0, 1, 2],
            isSkinned: false,
            checksum: 2);

        var document = BuildDocument(CreateScene(valid, malformedRigid), BuildSkeleton(1));

        Assert.Single(document.Skeletons);
        var primitives = document.Meshes.SelectMany(mesh => mesh.Primitives).ToArray();
        Assert.Single(primitives, primitive => primitive.Skin != null);
        Assert.Single(primitives, primitive => primitive.Skin == null);
    }

    [Fact]
    public void InvalidReferencedSkinnedSector_RejectsSkinGloballyWithoutOrphan()
    {
        var valid = CreateSector(
            [
                CreateVertex(Vector3.Zero, true, weight0: 1f),
                CreateVertex(Vector3.UnitX, false),
                CreateVertex(Vector3.UnitY, false)
            ],
            [0, 1, 2],
            isSkinned: true,
            checksum: 1);
        var invalid = CreateSector(
            [
                CreateVertex(Vector3.Zero, true, joint0: 1, weight0: 1f),
                CreateVertex(Vector3.UnitX, false),
                CreateVertex(Vector3.UnitY, false)
            ],
            [0, 1, 2],
            isSkinned: true,
            checksum: 2);

        var scene = CreateScene(valid, invalid);
        var document = BuildDocument(scene, BuildSkeleton(1));

        Assert.Empty(document.Skeletons);
        Assert.All(document.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));
        AssertRigidByteParity(scene, BuildSkeleton(1), coordinateScale: 1f);
    }

    [CorpusFact]
    public void RealAnlPigeon_ExplicitExactSkeletonEmitsFourJointSkinAndRestPoseIdentity()
    {
        var skinPath = paths.FindSampleFile(Thug2XboxBuild, "Anl_Pigeon.skin.xbx");
        var skeletonPath = paths.FindSampleFile(Thug2XboxBuild, "anl_pigeon.ske.xbx");
        Assert.SkipWhen(skinPath == null || skeletonPath == null,
            "THUG2 Xbox pigeon skin/skeleton fixtures are unavailable");

        var parsed = XbxSceneFile.Parse(skinPath!);
        Assert.Equal(46, parsed.TotalVertices);
        Assert.Equal(45, parsed.TotalTriangles);

        var rigid = ParseReal(skinPath!, skeletonPath: null);
        Assert.Empty(rigid.Skeletons);
        Assert.All(rigid.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));

        var skinned = ParseReal(skinPath!, skeletonPath);
        var skeleton = Assert.Single(skinned.Skeletons);
        Assert.Equal(4, skeleton.Bones.Count);
        Assert.Equal(45, skinned.TriangleCount);
        Assert.Single(
            skinned.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => primitive.Skin != null);
        AssertRestPoseIdentity(skinned, skeleton);

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(skinned);
        Assert.Equal(45, triangles);
        var glb = ModelRoot.ReadGLB(new MemoryStream(glbBytes!));
        Assert.Equal(4, Assert.Single(glb.LogicalSkins).JointsCount);
        Assert.Equal(46, glb.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive => primitive.GetVertexAccessor("POSITION").Count));

        var directorySelected = ParseReal(skinPath!, Path.GetDirectoryName(skeletonPath)!);
        Assert.Equal(4, Assert.Single(directorySelected.Skeletons).Bones.Count);

        var preparedSkeleton = SkeletonAssetLoader.Parse(
            Path.GetFileName(skeletonPath), File.ReadAllBytes(skeletonPath));
        var prepared = ParseReal(
            skinPath!, skeletonPath: null, preparedSkeleton: preparedSkeleton);
        Assert.Equal(4, Assert.Single(prepared.Skeletons).Bones.Count);
    }

    [CorpusFact]
    public void RealThawWpcPigeon_WithExplicitGcPigeonSkeletonEmitsSkin()
    {
        var skinPath = paths.FindSampleFile(ThawPcBuild, "anl_pigeon.skin.wpc");
        var skeletonPath = paths.FindSampleFile(ThawGcBuild, "anl_pigeon.ske.ngc");
        Assert.SkipWhen(skinPath == null || skeletonPath == null,
            "THAW PC pigeon and GC pigeon skeleton fixtures are unavailable");

        var parsed = ThawSceneFile.Parse(File.ReadAllBytes(skinPath!));
        Assert.Equal(46, parsed.TotalVertices);
        Assert.Equal(45, parsed.TotalTriangles);

        var rigid = ParseReal(skinPath!, skeletonPath: null);
        Assert.Empty(rigid.Skeletons);
        Assert.All(rigid.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));

        var document = ParseReal(skinPath!, skeletonPath);

        Assert.Equal(45, document.TriangleCount);
        Assert.Equal(4, Assert.Single(document.Skeletons).Bones.Count);
        Assert.Single(
            document.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => primitive.Skin != null);
        AssertRestPoseIdentity(document, Assert.Single(document.Skeletons));

        var (glbBytes, triangles) = new GltfModelExporter().BuildGlbBytes(document);
        Assert.Equal(45, triangles);
        var glb = ModelRoot.ReadGLB(new MemoryStream(glbBytes!));
        Assert.Equal(4, Assert.Single(glb.LogicalSkins).JointsCount);
        Assert.Equal(46, glb.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive => primitive.GetVertexAccessor("POSITION").Count));
    }

    [CorpusFact]
    public void RealThawNgcPigeon_PreparedRigMatchesDirectCliRigExactly()
    {
        var skinPath = paths.FindSampleFile(ThawGcBuild, "anl_pigeon.skin.ngc");
        var skeletonPath = paths.FindSampleFile(ThawGcBuild, "anl_pigeon.ske.ngc");
        Assert.SkipWhen(skinPath == null || skeletonPath == null,
            "THAW GameCube pigeon skin/skeleton fixtures are unavailable");

        var parsed = NgcSceneFile.Parse(File.ReadAllBytes(skinPath!));
        Assert.Equal(46, parsed.TotalVertices);
        Assert.Equal(45, parsed.TotalTriangles);
        Assert.Contains(parsed.Sectors, static sector => sector.IsSkinned);

        var rigid = ParseReal(skinPath!, skeletonPath: null);
        Assert.Empty(rigid.Skeletons);
        Assert.All(rigid.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));

        var direct = ParseReal(skinPath!, skeletonPath);
        var preparedSkeleton = SkeletonAssetLoader.Parse(
            Path.GetFileName(skeletonPath), File.ReadAllBytes(skeletonPath));
        var prepared = ParseReal(
            skinPath!, skeletonPath: null, preparedSkeleton: preparedSkeleton);

        Assert.Equal(45, prepared.TriangleCount);
        var skeleton = Assert.Single(prepared.Skeletons);
        Assert.Equal(4, skeleton.Bones.Count);
        Assert.Single(
            prepared.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => primitive.Skin != null);
        AssertRestPoseIdentity(prepared, skeleton);

        var (directGlb, directTriangles) = new GltfModelExporter().BuildGlbBytes(direct);
        var (preparedGlb, preparedTriangles) = new GltfModelExporter().BuildGlbBytes(prepared);
        Assert.Equal(45, directTriangles);
        Assert.Equal(directTriangles, preparedTriangles);
        Assert.Equal(directGlb, preparedGlb);

        var glb = ModelRoot.ReadGLB(new MemoryStream(preparedGlb!));
        Assert.Equal(4, Assert.Single(glb.LogicalSkins).JointsCount);
        Assert.Equal(46, glb.LogicalMeshes
            .SelectMany(mesh => mesh.Primitives)
            .Sum(primitive => primitive.GetVertexAccessor("POSITION").Count));
    }

    [CorpusFact]
    public void RealThawNgcPigeon_PreparedRigBlendHasRestBoundFourBoneArmature()
    {
        var helperPath = Environment.GetEnvironmentVariable("NEVERSOFT_BLENDER_HELPER");
        var importerPath = Path.Combine(
            AppContext.BaseDirectory, "BlenderExporter", "import_package.py");
        if (string.IsNullOrWhiteSpace(helperPath)
            || !File.Exists(helperPath)
            || !File.Exists(importerPath))
        {
            Assert.Skip(
                "Set NEVERSOFT_BLENDER_HELPER to Blender 5.1 and ensure " +
                "BlenderExporter/import_package.py is copied to run this GameCube Blend oracle.");
        }

        var skinPath = paths.FindSampleFile(ThawGcBuild, "anl_pigeon.skin.ngc");
        var skeletonPath = paths.FindSampleFile(ThawGcBuild, "anl_pigeon.ske.ngc");
        Assert.SkipWhen(skinPath == null || skeletonPath == null,
            "THAW GameCube pigeon skin/skeleton fixtures are unavailable");

        var preparedSkeleton = SkeletonAssetLoader.Parse(
            Path.GetFileName(skeletonPath), File.ReadAllBytes(skeletonPath));
        var document = ParseReal(
            skinPath!, skeletonPath: null, preparedSkeleton: preparedSkeleton);

        using var temp = new BlendTempDirectory();
        var result = ModelExportService.Export(document, new MeshExportRequest
        {
            OutputDirectory = temp.Path,
            OutputStem = "anl_pigeon_gc",
            Format = MeshOutputFormat.Blend,
            BlenderHelperPath = helperPath,
            CancellationToken = TestContext.Current.CancellationToken
        });

        Assert.Equal(45, result.Triangles);
        var blendPath = Assert.Single(result.OutputPaths);
        Assert.True(File.Exists(blendPath));

        var report = InspectNgcBlend(helperPath!, blendPath, temp.Path);
        Assert.Equal(1, report.Armatures);
        Assert.Equal(4, report.Bones);
        Assert.Equal(1, report.ArmatureModifiedMeshes);
        Assert.Equal(45, report.Triangles);
        Assert.InRange(report.RestBoneError, 0f, 1e-5f);
        Assert.InRange(report.RestVertexDelta, 0f, 1e-5f);
    }

    private static NgcBlendReport InspectNgcBlend(
        string helperPath,
        string blendPath,
        string tempDirectory)
    {
        var inspectScript = Path.Combine(tempDirectory, "inspect_ngc_skin.py");
        var reportPath = Path.Combine(tempDirectory, "ngc_skin_report.json");
        File.WriteAllText(inspectScript, NgcBlendInspectionScript);

        using var process = new Process();
        process.StartInfo.FileName = helperPath;
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.CreateNoWindow = true;
        process.StartInfo.ArgumentList.Add("--background");
        process.StartInfo.ArgumentList.Add(blendPath);
        process.StartInfo.ArgumentList.Add("--python-exit-code");
        process.StartInfo.ArgumentList.Add("1");
        process.StartInfo.ArgumentList.Add("--python");
        process.StartInfo.ArgumentList.Add(inspectScript);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(reportPath);

        Assert.True(process.Start(), "Failed to start Blender for GameCube skin inspection.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && File.Exists(reportPath),
            $"Blender GameCube skin inspection failed ({process.ExitCode})." +
            Environment.NewLine + stdout + Environment.NewLine + stderr);
        return JsonSerializer.Deserialize<NgcBlendReport>(File.ReadAllText(reportPath))!;
    }

    private static ModelDocument ParseReal(
        string skinPath,
        string? skeletonPath,
        Ps2Skeleton? preparedSkeleton = null)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = new FileSystemAssetSource(skinPath),
            FileName = Path.GetFileName(skinPath),
            OutputStem = "Anl_Pigeon",
            SourceKind = ModelSourceKind.XbxScene,
            SkeletonPath = skeletonPath,
            PreparedSkeleton = preparedSkeleton
        });
    }

    private static ModelDocument BuildDocument(
        ParsedXbxScene scene,
        Ps2Skeleton? skeleton,
        float coordinateScale = 1f)
    {
        var document = new ModelDocument { Name = "xbx_skin", SourceKind = ModelSourceKind.XbxScene };
        XbxGeometryWriter.PopulateXbxScene(
            document, scene, textureProvider: null, coordinateScale, skeleton);
        return document;
    }

    private static void AssertRigidByteParity(
        ParsedXbxScene scene,
        Ps2Skeleton skeleton,
        float coordinateScale)
    {
        var expected = BuildDocument(scene, null, coordinateScale);
        var actual = BuildDocument(scene, skeleton, coordinateScale);
        Assert.Empty(actual.Skeletons);
        Assert.All(actual.Meshes.SelectMany(mesh => mesh.Primitives),
            primitive => Assert.Null(primitive.Skin));

        var expectedBytes = new GltfModelExporter().BuildGlbBytes(expected).GlbBytes;
        var actualBytes = new GltfModelExporter().BuildGlbBytes(actual).GlbBytes;
        Assert.Equal(expectedBytes, actualBytes);
    }

    private static ParsedXbxScene CreateTriangleScene(
        XbxVertex v0,
        XbxVertex v1,
        XbxVertex v2,
        bool isSkinnedSector = true)
    {
        return CreateScene(CreateSector(
            [v0, v1, v2],
            [0, 1, 2],
            isSkinnedSector));
    }

    private static ParsedXbxScene CreateScene(params XbxSector[] sectors)
    {
        return new ParsedXbxScene
        {
            Materials = [],
            Links = [],
            Sectors = sectors
        };
    }

    private static XbxSector CreateSector(
        XbxVertex[] vertices,
        ushort[] faceIndices,
        bool isSkinned,
        uint checksum = 0x1234u)
    {
        return new XbxSector
        {
            Checksum = checksum,
            Flags = isSkinned ? 0x10 : 0,
            Meshes =
            [
                new XbxMesh
                {
                    MaterialChecksum = 0x5678u,
                    Vertices = vertices,
                    FaceIndices = faceIndices,
                    IsPreTriangulated = true
                }
            ]
        };
    }

    private static XbxVertex CreateVertex(
        Vector3 position,
        bool hasSkinData,
        int joint0 = 0,
        float weight0 = 0f,
        int joint3 = 0,
        float weight3 = 0f)
    {
        return new XbxVertex
        {
            Position = position,
            Normal = Vector3.UnitZ,
            Color = Vector4.One,
            HasNormal = true,
            HasColor = true,
            HasSkinData = hasSkinData,
            BoneIndex0 = joint0,
            BoneIndex3 = joint3,
            BoneWeight0 = weight0,
            BoneWeight3 = weight3
        };
    }

    private static Ps2Skeleton BuildSkeleton(int count)
    {
        var bones = new Ps2Bone[count];
        for (var index = 0; index < count; index++)
        {
            bones[index] = new Ps2Bone
            {
                NameChecksum = 0x100u + (uint)index,
                ParentChecksum = index == 0 ? 0 : 0x100u + (uint)(index - 1),
                FlipChecksum = 0x100u + (uint)index,
                ParentIndex = index - 1,
                LocalRotation = Quaternion.Identity,
                LocalTranslation = index == 0 ? Vector3.Zero : Vector3.UnitY,
                InverseBindMatrix = Matrix4x4.CreateTranslation(0f, -index, 0f)
            };
        }

        return new Ps2Skeleton { Version = 2, Flags = 0, Bones = bones };
    }

    private static void AssertRestPoseIdentity(ModelDocument document, ModelSkeleton skeleton)
    {
        var world = new Matrix4x4[skeleton.Bones.Count];
        for (var index = 0; index < skeleton.Bones.Count; index++)
        {
            var bone = skeleton.Bones[index];
            world[index] = bone.ParentIndex < 0
                ? bone.LocalTransform
                : bone.LocalTransform * world[bone.ParentIndex];
            AssertMatrixClose(Matrix4x4.Identity, bone.InverseBindMatrix * world[index]);
        }

        foreach (var primitive in document.Meshes.SelectMany(mesh => mesh.Primitives))
        {
            if (primitive.Skin is not { } skin) continue;
            for (var index = 0; index < primitive.Vertices.Length; index++)
            {
                var source = primitive.Vertices[index].Position;
                var influence = skin.Influences[index];
                var restored = Vector3.Zero;
                AddWeighted(ref restored, source, influence.Joint0, influence.Weight0, skeleton, world);
                AddWeighted(ref restored, source, influence.Joint1, influence.Weight1, skeleton, world);
                AddWeighted(ref restored, source, influence.Joint2, influence.Weight2, skeleton, world);
                AddWeighted(ref restored, source, influence.Joint3, influence.Weight3, skeleton, world);
                AssertVectorClose(source, restored);
            }
        }
    }

    private static void AddWeighted(
        ref Vector3 result,
        Vector3 source,
        int joint,
        float weight,
        ModelSkeleton skeleton,
        Matrix4x4[] world)
    {
        if (weight <= 0f) return;
        result += Vector3.Transform(
            source, skeleton.Bones[joint].InverseBindMatrix * world[joint]) * weight;
    }

    private static void AssertMatrixClose(Matrix4x4 expected, Matrix4x4 actual)
    {
        var expectedValues = new[]
        {
            expected.M11, expected.M12, expected.M13, expected.M14,
            expected.M21, expected.M22, expected.M23, expected.M24,
            expected.M31, expected.M32, expected.M33, expected.M34,
            expected.M41, expected.M42, expected.M43, expected.M44
        };
        var actualValues = new[]
        {
            actual.M11, actual.M12, actual.M13, actual.M14,
            actual.M21, actual.M22, actual.M23, actual.M24,
            actual.M31, actual.M32, actual.M33, actual.M34,
            actual.M41, actual.M42, actual.M43, actual.M44
        };
        for (var index = 0; index < expectedValues.Length; index++)
            Assert.InRange(MathF.Abs(expectedValues[index] - actualValues[index]), 0f, 1e-4f);
    }

    private static void AssertVectorClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(MathF.Abs(expected.X - actual.X), 0f, 1e-4f);
        Assert.InRange(MathF.Abs(expected.Y - actual.Y), 0f, 1e-4f);
        Assert.InRange(MathF.Abs(expected.Z - actual.Z), 0f, 1e-4f);
    }

    private sealed record NgcBlendReport(
        int Armatures,
        int Bones,
        int ArmatureModifiedMeshes,
        int Triangles,
        float RestBoneError,
        float RestVertexDelta);

    private const string NgcBlendInspectionScript = """
        import bpy
        import json
        import sys

        report_path = sys.argv[sys.argv.index('--') + 1]
        scene = bpy.context.scene
        armatures = [obj for obj in scene.objects if obj.type == 'ARMATURE']
        armature = armatures[0] if len(armatures) == 1 else None
        bound_meshes = []
        if armature is not None:
            bound_meshes = [
                obj for obj in scene.objects
                if obj.type == 'MESH' and any(
                    modifier.type == 'ARMATURE' and modifier.object == armature
                    for modifier in obj.modifiers
                )
            ]

        triangle_count = 0
        for obj in bound_meshes:
            obj.data.calc_loop_triangles()
            triangle_count += len(obj.data.loop_triangles)

        rest_bone_error = 0.0
        if armature is not None:
            for pose_bone in armature.pose.bones:
                rest_bone = armature.data.bones[pose_bone.name]
                rest_bone_error = max(
                    rest_bone_error,
                    max(
                        abs(pose_bone.matrix[row][column] - rest_bone.matrix_local[row][column])
                        for row in range(4)
                        for column in range(4)
                    ),
                )

        scene.frame_set(scene.frame_start)
        bpy.context.view_layer.update()
        depsgraph = bpy.context.evaluated_depsgraph_get()
        rest_vertex_delta = 0.0
        for obj in bound_meshes:
            evaluated = obj.evaluated_get(depsgraph)
            evaluated_mesh = evaluated.to_mesh()
            if len(evaluated_mesh.vertices) != len(obj.data.vertices):
                rest_vertex_delta = float('inf')
            else:
                for index, vertex in enumerate(obj.data.vertices):
                    source_world = obj.matrix_world @ vertex.co
                    evaluated_world = evaluated.matrix_world @ evaluated_mesh.vertices[index].co
                    rest_vertex_delta = max(
                        rest_vertex_delta,
                        (evaluated_world - source_world).length,
                    )
            evaluated.to_mesh_clear()

        with open(report_path, 'w', encoding='utf-8') as stream:
            json.dump({
                'Armatures': len(armatures),
                'Bones': len(armature.data.bones) if armature is not None else 0,
                'ArmatureModifiedMeshes': len(bound_meshes),
                'Triangles': triangle_count,
                'RestBoneError': rest_bone_error,
                'RestVertexDelta': rest_vertex_delta,
            }, stream)
        """;

    private sealed class BlendTempDirectory : IDisposable
    {
        public BlendTempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-ngc-skin-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, true);
            }
            catch
            {
                // Blender can briefly retain a file handle during process teardown.
            }
        }
    }
}
