using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Rendering;
using NeversoftMultitool.Core.QbKey;
using NeversoftMultitool.Tests.Helpers;
using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxArticulatedGeometryRegressionTests(TestPaths paths)
{
    private const string BuildName = "Spider-Man (2000-9-1, PSX - Final)";
    private const string PcBuildName = "Spider-Man (2001-9-17, PC - Final)";
    private static readonly int[] CarnageHandObjects = [13, 14, 17, 18];

    [Fact]
    public void SplineTextureSelection_FollowsMappedHiddenTemplateInsteadOfTextureOrder()
    {
        const uint templateHash = 0x12345678u;
        const uint reorderedDecoyHash = 0x87654321u;
        var templateRead = new PsxFaceReadInfo
        {
            RawFaceIndex = 0,
            Offset = 0,
            Flags = 0x0083,
            Length = 28,
            BytesConsumed = 28,
            UnderreadBytes = 0,
            OverreadBytes = 0,
            IsLengthAligned = true,
            TextureHash = templateHash,
            TextureCoordinates =
            [
                new PsxTextureCoordinate(0, 0),
                new PsxTextureCoordinate(0, 63),
                new PsxTextureCoordinate(63, 0),
                new PsxTextureCoordinate(63, 63)
            ],
            RejectionReason = "invisible (M3dInit STP toggle)"
        };
        var file = new PsxMeshFile
        {
            Version = 0x04,
            Objects = [],
            Meshes =
            [
                new PsxMesh
                {
                    Vertices = [],
                    Normals = [],
                    Faces = [],
                    FaceReadInfos = [templateRead]
                }
            ],
            MeshNameHashes = [],
            // The decoy is deliberately last: the old reverse-slot heuristic
            // selected it even though no hidden template face referenced it.
            TextureHashes = [templateHash, reorderedDecoyHash]
        };
        var squarePng = CreateSolidPng(64, 64);

        Assert.Equal(
            templateHash,
            PsxSplineAppendageGeometry.FindTubeTextureHash(file, _ => squarePng));
    }

    [Fact]
    public void SplineTransportFrames_RemainContinuousAcrossReferenceAxisThreshold()
    {
        Vector3[] centers =
        [
            new(0f, 0f, 0f),
            new(0.5f, 0.85f, 0f),
            new(0.7f, 1.85f, 0.02f),
            new(0.55f, 2.85f, 0.08f),
            new(0f, 3.7f, 0.15f)
        ];

        var frames = PsxSplineAppendageGeometry.BuildTransportFrames(centers);

        Assert.Equal(centers.Length, frames.Count);
        Assert.All(frames, static frame =>
        {
            Assert.InRange(MathF.Abs(frame.Normal.Length() - 1f), 0f, 1e-5f);
            Assert.InRange(MathF.Abs(frame.Binormal.Length() - 1f), 0f, 1e-5f);
            Assert.InRange(MathF.Abs(Vector3.Dot(frame.Normal, frame.Binormal)), 0f, 1e-5f);
        });
        for (var index = 1; index < frames.Count; index++)
            Assert.True(Vector3.Dot(frames[index - 1].Normal, frames[index].Normal) > 0f);
    }

    [Fact]
    public void Carnage_FromCdWad_TreatsAxeHandsAsAlternateMeshes()
    {
        using var fixture = OpenArchiveEntry("carnage.psx");
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);

        // The ordinary and axe hands are stitched to the same two wrist
        // joints, but intentionally have different silhouettes/topology.
        // They are pose alternatives, not four simultaneous hands.
        AssertMeshTopology(file!, 13, expectedVertices: 20, expectedFaces: 25);
        AssertMeshTopology(file, 14, expectedVertices: 27, expectedFaces: 40);
        AssertMeshTopology(file, 17, expectedVertices: 20, expectedFaces: 25);
        AssertMeshTopology(file, 18, expectedVertices: 27, expectedFaces: 40);
        Assert.All(CarnageHandObjects, objectIndex =>
        {
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex);
            Assert.Contains(file.Meshes[meshIndex].Vertices,
                static vertex => PsxMeshSemantics.IsExactStitchedReference(vertex.Type));
        });

        Assert.Equal(
            [14, 18],
            PsxMeshSemantics.FindAlternateLeafObjectIndices(file)
                .OrderBy(static index => index)
                .ToArray());

        var document = Parse(fixture.Source, "carnage.psx");
        Assert.Equal(556, document.TriangleCount);

        var handGroups = document.VisibilityGroups
            .Where(static group => group.Source == ModelVisibilityGroupSource.AlternateGeometry)
            .ToArray();
        Assert.Equal(2, handGroups.Length);
        Assert.All(handGroups, static group =>
        {
            Assert.False(group.DefaultEnabled);
            Assert.False(group.IsEnabled);
            Assert.StartsWith("psx.alt.", group.Id, StringComparison.Ordinal);
            Assert.Contains("instead of", group.Label, StringComparison.Ordinal);
            Assert.Null(group.ExclusiveSetId);
        });
        Assert.Contains(handGroups,
            static group => group.SourceReference.Contains("13 / 14", StringComparison.Ordinal));
        Assert.Contains(handGroups,
            static group => group.SourceReference.Contains("17 / 18", StringComparison.Ordinal));

        var axeHandOverrides = handGroups.ToDictionary(
            static group => group.Id,
            static _ => true,
            StringComparer.Ordinal);
        var axeDocument = Parse(fixture.Source, "carnage.psx", axeHandOverrides);
        Assert.Equal(
            handGroups.Select(static group => group.Id),
            axeDocument.VisibilityGroups.Select(static group => group.Id));
        Assert.All(axeDocument.VisibilityGroups, static group => Assert.True(group.IsEnabled));

        var ordinaryHandTriangles = TriangleCount(file, 13) + TriangleCount(file, 17);
        var axeHandTriangles = TriangleCount(file, 14) + TriangleCount(file, 18);
        Assert.Equal(
            document.TriangleCount - ordinaryHandTriangles + axeHandTriangles,
            axeDocument.TriangleCount);
    }

    [Theory]
    [InlineData("scorpion.psx", 1, 17)]
    [InlineData("docock.psx", 4, 18)]
    [InlineData("superock.psx", 4, 17)]
    public void SplineSupers_FromCdWad_HaveConservativeSevenControllerChains(
        string entryName,
        int expectedChainCount,
        int firstControllerObject)
    {
        using var fixture = OpenArchiveEntry(entryName);
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);

        var chains = PsxSplineAppendageGeometry.FindControllerChains(file!);

        Assert.Equal(expectedChainCount, chains.Count);
        Assert.Equal(firstControllerObject, chains[0].ObjectIndices[0]);
        Assert.Equal(file.Objects.Count - 1, chains[^1].ObjectIndices[^1]);
        Assert.All(chains, static chain => Assert.Equal(7, chain.ObjectIndices.Count));
        Assert.Equal(
            Enumerable.Range(firstControllerObject, expectedChainCount * 7),
            chains.SelectMany(static chain => chain.ObjectIndices));
    }

    [Theory]
    [InlineData("control.psx")]
    [InlineData("carnage.psx")]
    [InlineData("spidey.psx")]
    public void OrdinaryCharacters_FromCdWad_DoNotMatchSplineControllerSignature(string entryName)
    {
        using var fixture = OpenArchiveEntry(entryName);
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);
        Assert.Empty(PsxSplineAppendageGeometry.FindControllerChains(file!));
    }

    [Theory]
    [InlineData("scorpion.psx", 1, 762)]
    [InlineData("docock.psx", 4, 1496)]
    [InlineData("superock.psx", 4, 1536)]
    public void SplineSupers_WithDrivingAnimation_ReplaceControllerBoxesWithSkinnedGeometry(
        string entryName,
        int expectedChainCount,
        int expectedTriangles)
    {
        using var fixture = OpenArchiveEntry(entryName);
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);
        var chains = PsxSplineAppendageGeometry.FindControllerChains(file!);
        Assert.Equal(expectedChainCount, chains.Count);

        var document = ParseAnimated(fixture.Source, entryName, file.Objects.Count);

        Assert.Equal(expectedTriangles, document.TriangleCount);
        Assert.Single(document.Skeletons);
        Assert.Equal(file.Objects.Count, document.Skeletons[0].Bones.Count);
        var emittedAnimation = Assert.Single(document.Animations);
        Assert.NotEmpty(emittedAnimation.Channels);

        // Every controller still drives the generated tube, even though its
        // six authored placeholder faces are no longer rendered.
        var weightedJoints = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Where(static primitive => primitive.Skin != null)
            .SelectMany(static primitive => primitive.Skin!.Influences)
            .SelectMany(PositiveJointIndices)
            .ToHashSet();
        Assert.All(
            chains.SelectMany(static chain => chain.ObjectIndices),
            controllerObject => Assert.Contains(controllerObject, weightedJoints));

        if (expectedChainCount == 4)
        {
            var clawBytes = fixture.Source.TryReadCompanion("claw.psx");
            Assert.NotNull(clawBytes);
            var claw = PsxMeshFile.Parse(clawBytes!);
            Assert.NotNull(claw);
            Assert.Equal([0x00000002u, 0xF65F4E07u, 0x9809FFF5u], claw!.TextureHashes);
            var clawMesh = Assert.Single(claw.Meshes);

            // The stream declares 24 records. The last two are opaque records
            // with raw bit 7 set, so the loader's STP XOR deliberately makes
            // them invisible to the ordinary mesh path. They preload the two
            // 64x64 banded runtime spline skins; the 22 drawable claw faces
            // themselves all use slot zero.
            Assert.Equal(24, clawMesh.FaceReadInfos.Count);
            Assert.Equal(22, clawMesh.Faces.Count);
            Assert.Equal([0x0083, 0x0083],
                clawMesh.FaceReadInfos.Skip(22).Select(static info => info.Flags));
            Assert.All(clawMesh.FaceReadInfos.Skip(22), static info =>
            {
                Assert.False(info.IsAccepted);
                Assert.Equal("invisible (M3dInit STP toggle)", info.RejectionReason);
            });
            Assert.All(clawMesh.Faces, static face =>
            {
                Assert.True(face.IsTextured);
                Assert.Equal(0x00000002u, face.TextureHash);
            });
            Assert.Equal(40, clawMesh.Faces.Sum(static face => face.IsQuad ? 2 : 1));

            // Each of the four controller chains instances those 40 triangles.
            // The claw's embedded 32x32 image must be resolved from claw.psx,
            // and every emitted vertex must retain a real authored UV rather
            // than the former all-zero placeholder coordinates.
            var clawTextureIndex = Assert.Single(
                document.Textures.Select((texture, index) => (Texture: texture, Index: index)),
                static item => item.Texture.NativeChecksum == 0x00000002u
                               && item.Texture.PngBytes is { } bytes
                               && ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes)
                                   == (Width: 32, Height: 32))
                .Index;
            var clawMaterialIndex = Assert.Single(
                document.Materials.Select((material, index) => (Material: material, Index: index)),
                item => item.Material.TextureIndex == clawTextureIndex)
                .Index;
            var clawPrimitive = Assert.Single(
                document.Meshes.SelectMany(static mesh => mesh.Primitives),
                primitive => primitive.MaterialIndex == clawMaterialIndex);
            Assert.Equal(expectedChainCount * 40, clawPrimitive.TriangleCount);
            Assert.All(clawPrimitive.Vertices,
                static vertex => Assert.NotEqual(Vector2.Zero, vertex.TexCoord));

            // The generated tubes must use the authored runtime strip bundled
            // in claw.psx instead of the former flat-grey material. Selection
            // is structural (unused square claw texture), not character-name
            // or level-name hardcoding. Longitudinal UVs repeat the strip and
            // the duplicated closing ring keeps every triangle local in V.
            var clawTextureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
                fixture.Source, "claw.psx", clawBytes!);
            const uint expectedTubeTextureHash = 0x9809FFF5u;
            Assert.Equal(
                expectedTubeTextureHash,
                PsxSplineAppendageGeometry.FindTubeTextureHash(
                    claw, clawTextureProvider));

            var tubeTextureIndex = Assert.Single(
                document.Textures.Select((texture, index) => (Texture: texture, Index: index)),
                static item => item.Texture.NativeChecksum == expectedTubeTextureHash
                               && item.Texture.PngBytes is { } bytes
                               && ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes)
                                   == (Width: 64, Height: 64))
                .Index;
            var tubeMaterialIndex = Assert.Single(
                document.Materials.Select((material, index) => (Material: material, Index: index)),
                item => item.Material.TextureIndex == tubeTextureIndex)
                .Index;
            var tubePrimitive = Assert.Single(
                document.Meshes.SelectMany(static mesh => mesh.Primitives),
                primitive => primitive.MaterialIndex == tubeMaterialIndex);
            Assert.Equal(expectedChainCount * 208, tubePrimitive.TriangleCount);
            Assert.True(tubePrimitive.Vertices.Max(static vertex => vertex.TexCoord.X) > 1f);
            Assert.Equal(0f, tubePrimitive.Vertices.Min(static vertex => vertex.TexCoord.Y));
            Assert.Equal(1f, tubePrimitive.Vertices.Max(static vertex => vertex.TexCoord.Y));
            Assert.All(
                tubePrimitive.Indices.Chunk(3),
                triangle =>
                {
                    var v = triangle
                        .Select(index => tubePrimitive.Vertices[index].TexCoord.Y)
                        .ToArray();
                    Assert.True(v.Max() - v.Min() <= 1f / 8f + 1e-6f);
                });
        }
        else
        {
            // Scorpion's authored hook is the sole embedded tip and every one
            // of its faces uses the same 64x64 blue/green strip. Its generated
            // tail uses that source-associated image rather than the white
            // fallback material.
            const uint expectedTailTextureHash = 0x35A7A03Du;
            var tipPlacements = PsxSplineAppendageGeometry.FindEmbeddedTipPlacements(
                file, chains);
            Assert.Single(tipPlacements);
            var textureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
                fixture.Source, entryName, fixture.Source.ReadBytes());
            Assert.Equal(
                expectedTailTextureHash,
                PsxSplineAppendageGeometry.FindEmbeddedTailTextureHash(
                    file, tipPlacements, textureProvider));

            var tailTextureIndex = Assert.Single(
                document.Textures.Select((texture, index) => (Texture: texture, Index: index)),
                static item => item.Texture.NativeChecksum == expectedTailTextureHash
                               && item.Texture.PngBytes is { } bytes
                               && ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes)
                                   == (Width: 64, Height: 64))
                .Index;
            var tailMaterialIndex = Assert.Single(
                document.Materials.Select((material, index) => (Material: material, Index: index)),
                item => item.Material.TextureIndex == tailTextureIndex)
                .Index;
            var tailPrimitive = Assert.Single(
                document.Meshes.SelectMany(static mesh => mesh.Primitives),
                primitive => primitive.MaterialIndex == tailMaterialIndex);

            // The endpoint contributes 28 authored triangles and the tube
            // contributes 208. Geometric normals and the explicit radial
            // normals must point into the same hemisphere on every tube face.
            Assert.Equal(236, tailPrimitive.TriangleCount);
            Assert.Equal(236, CountTrianglesWithMatchingNormals(tailPrimitive));
            Assert.True(tailPrimitive.Vertices.Max(static vertex => vertex.TexCoord.X) > 1f);

            var (glbBytes, _) = new GltfModelExporter().BuildGlbBytes(document);
            Assert.NotNull(glbBytes);
            using var stream = new MemoryStream(glbBytes!, writable: false);
            var model = ModelRoot.ReadGLB(stream);
            var gltfAnimation = Assert.Single(model.LogicalAnimations);
            var matchingAnimatedNormals = new List<int>();
            var bindScene = GlbModelLoader.Load(model, animation: null, time: 0f);
            var bindTail = Assert.Single(
                bindScene.Submeshes,
                static submesh => submesh.TriangleCount == 236
                                  && submesh.TextureWidth == 64
                                  && submesh.TextureHeight == 64);
            matchingAnimatedNormals.Add(CountTrianglesWithMatchingNormals(bindTail));
            foreach (var amount in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
            {
                var time = gltfAnimation.Duration * amount;
                var scene = GlbModelLoader.Load(model, gltfAnimation, time);
                var animatedTail = Assert.Single(
                    scene.Submeshes,
                    static submesh => submesh.TriangleCount == 236
                                      && submesh.TextureWidth == 64
                                      && submesh.TextureHeight == 64);
                matchingAnimatedNormals.Add(CountTrianglesWithMatchingNormals(animatedTail));
            }
            Assert.Equal(Enumerable.Repeat(236, 6), matchingAnimatedNormals);
        }
    }

    [Fact]
    public void PcDocOck_FromDataPkr_OrientsClawProngsAlongTentacleTangent()
    {
        using var fixture = OpenArchiveEntry(
            "docock.psx", PcBuildName, "data.pkr");
        if (fixture == null)
            return;

        var fileBytes = fixture.Source.ReadBytes();
        var file = PsxMeshFile.Parse(fileBytes);
        Assert.NotNull(file);
        Assert.Equal((ushort)0x06, file!.Version);
        var chains = PsxSplineAppendageGeometry.FindControllerChains(file);
        Assert.Equal(4, chains.Count);

        var clawBytes = fixture.Source.TryReadCompanion("claw.psx");
        Assert.NotNull(clawBytes);
        var claw = PsxMeshFile.Parse(clawBytes!);
        Assert.NotNull(claw);
        var clawMesh = Assert.Single(claw!.Meshes);

        // The archive-authored claw extends 26 units down local -Z from its
        // attachment origin, but only 12 units up +Z. Thus -Z is the distal
        // prong direction; mapping +Z to the endpoint tangent points the claw
        // back into the tentacle.
        Assert.Equal((short)-26, clawMesh.Vertices.Min(static vertex => vertex.RawZ));
        Assert.Equal((short)12, clawMesh.Vertices.Max(static vertex => vertex.RawZ));
        Assert.Equal(-1, PsxSplineAppendageGeometry.DetermineTipForwardSign(clawMesh));

        var document = new ModelDocument { Name = "docock" };
        var textureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
            fixture.Source, "docock.psx", fileBytes);
        var clawTextureProvider = MeshCompanionResolver.BuildPsxTextureProvider(
            fixture.Source, "claw.psx", clawBytes);
        PsxSkinnedGeometryWriter.PopulatePsxSkinned(
            document,
            file,
            pshFile: null,
            textureProvider,
            flatSkeleton: false,
            flatBoneIndices: null,
            claw,
            clawTextureProvider,
            hiddenObjectIndices: null,
            reconstructSplineAppendages: true);

        var clawTextureIndex = Assert.Single(
            document.Textures.Select((texture, index) => (Texture: texture, Index: index)),
            static item => item.Texture.NativeChecksum == 0x00000002u
                           && item.Texture.PngBytes is { } bytes
                           && ModelDocumentGeometryAdapter.TryExtractPngDimensions(bytes)
                               == (Width: 32, Height: 32))
            .Index;
        var clawMaterialIndex = Assert.Single(
            document.Materials.Select((material, index) => (Material: material, Index: index)),
            item => item.Material.TextureIndex == clawTextureIndex)
            .Index;
        var clawPrimitive = Assert.Single(
            document.Meshes.SelectMany(static mesh => mesh.Primitives),
            primitive => primitive.MaterialIndex == clawMaterialIndex);
        Assert.Equal(160, clawPrimitive.TriangleCount);
        Assert.NotNull(clawPrimitive.Skin);

        foreach (var chain in chains)
        {
            var endpointJoint = chain.ObjectIndices[^1];
            var center = PsxMeshSemantics.ToGltfPosition(chain.Centers[^1]);
            var tangent = Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(
                chain.Centers[^1] - chain.Centers[^2]));
            var projections = clawPrimitive.Vertices
                .Zip(clawPrimitive.Skin!.Influences)
                .Where(item => item.Second.Joint0 == endpointJoint
                               && item.Second.Weight0 > 0f)
                .Select(item => Vector3.Dot(item.First.Position - center, tangent))
                .ToArray();

            Assert.NotEmpty(projections);
            Assert.True(projections.Max() > 10f);
            Assert.InRange(projections.Min(), -1e-4f, 1e-4f);
        }

        // The sign correction is a rotation, not a one-axis reflection: its
        // transformed local basis remains right-handed, preserving winding.
        var placement = PsxSplineAppendageGeometry.CreateTipPlacement(
            chains[0], PsxSplineAppendageGeometry.DetermineTipForwardSign(clawMesh));
        var transformedX = placement.TransformDirection(Vector3.UnitX);
        var transformedY = placement.TransformDirection(Vector3.UnitY);
        var transformedZ = placement.TransformDirection(Vector3.UnitZ);
        Assert.True(Vector3.Dot(Vector3.Cross(transformedX, transformedY), transformedZ) > 0.999f);
    }

    [Theory]
    [InlineData("scorpion.psx")]
    [InlineData("docock.psx")]
    [InlineData("superock.psx")]
    public void SplineSupers_WithoutDrivingAnimation_OmitEditorRigAndGeneratedPole(
        string entryName)
    {
        using var fixture = OpenArchiveEntry(entryName);
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);
        var chains = PsxSplineAppendageGeometry.FindControllerChains(file!);
        Assert.NotEmpty(chains);

        var document = Parse(fixture.Source, entryName);
        Assert.Empty(document.Animations);
        var weightedJoints = document.Meshes
            .SelectMany(static mesh => mesh.Primitives)
            .Where(static primitive => primitive.Skin != null)
            .SelectMany(static primitive => primitive.Skin!.Influences)
            .SelectMany(PositiveJointIndices)
            .ToHashSet();

        // The bind positions are a straight editor rig shared by all chains.
        // Without a clip there is no honest tail/tentacle pose to generate, so
        // omit both that rig and the misleading overlapping procedural poles.
        Assert.All(
            chains.SelectMany(static chain => chain.ObjectIndices),
            controllerObject => Assert.DoesNotContain(controllerObject, weightedJoints));
    }

    [Fact]
    public void L8a4_FromCdWad_PreservesButInitiallyHidesKevinWhatIfGeometry()
    {
        using var fixture = OpenArchiveEntry("l8a4_g.psx");
        if (fixture == null)
            return;

        var file = PsxMeshFile.Parse(fixture.Source.ReadBytes());
        Assert.NotNull(file);
        var ribbon = file!.Meshes[47];

        // This is deliberately three long quads in the Kevin_01 What-If
        // alternate. It is not malformed triangle data to cull or triangulate
        // differently, so the raw parse must preserve it even though the
        // companion trigger hides the Kevin_00..05 group by default.
        Assert.Equal(8, ribbon.Vertices.Count);
        Assert.Equal(3, ribbon.Faces.Count);
        Assert.All(ribbon.Faces, static face =>
        {
            Assert.True(face.IsQuad);
            Assert.True(face.IsTextured);
            Assert.Equal((ushort)0x3823, face.Flags);
            Assert.Equal(0x7398654Bu, face.TextureHash);
            Assert.NotNull(face.TextureWibble);
        });

        var longestAuthoredEdge = ribbon.Faces.Max(face =>
            Enumerable.Range(0, 4)
                .Select(slot => Vector3.Distance(
                    Position(ribbon, PsxGeometryHelpers.GetPsxFaceVertexIndex(face, slot)),
                    Position(ribbon, PsxGeometryHelpers.GetPsxFaceVertexIndex(face, (slot + 1) % 4))))
                .Max());
        Assert.True(longestAuthoredEdge > 100f);

        Assert.Equal(
            Enumerable.Range(0, 6).Select(index => QbKey.Hash($"Kevin_{index:D2}")),
            file.MeshNameHashes.Skip(46).Take(6));

        var authoredTriangleCount = file.Objects
            .Where(obj => obj.MeshIndex < file.Meshes.Count)
            .Sum(obj => file.Meshes[obj.MeshIndex].Faces.Sum(
                static face => face.IsQuad ? 2 : 1));
        Assert.Equal(836, authoredTriangleCount);

        var initiallyHidden = PsxTriggerVisibilityResolver.FindInitiallyHiddenMeshes(
            fixture.Source, "l8a4_g.psx", file);
        Assert.All(Enumerable.Range(46, 6),
            meshIndex => Assert.Contains(meshIndex, initiallyHidden));
        var initiallyHiddenTriangles = file.Objects
            .Where(obj => initiallyHidden.Contains(obj.MeshIndex))
            .Sum(obj => file.Meshes[obj.MeshIndex].Faces.Sum(
                static face => face.IsQuad ? 2 : 1));
        Assert.Equal(384, initiallyHiddenTriangles);

        var objectEntry = fixture.Source.Backend.FindEntry("l8a4_o.psx");
        Assert.NotNull(objectEntry);
        var objectDocument = Parse(
            new ArchiveAssetSource(fixture.Source.Backend, objectEntry!),
            "l8a4_o.psx");
        Assert.Equal(766, objectDocument.TriangleCount);

        var document = Parse(fixture.Source, "l8a4_g.psx");
        // With a single unambiguous restart, apply the whole authored default
        // state: Kevin_ plus the A/B/C/D On/Glowing alternates start hidden.
        // Opening the geometry entry also places the level-object bank (stored
        // positions plus PLATFORM re-instances); the bank remains independently
        // openable above as a set of model definitions.
        // Re-pinned 2026-07-23: +316 from the POWERUP layer's items.psx pickups
        // (folded into the placed-object total below).
        Assert.Equal(2_495, document.TriangleCount);
        const int placedObjectTriangleCount = 2_043;
        Assert.Equal(
            placedObjectTriangleCount,
            document.TriangleCount - (authoredTriangleCount - initiallyHiddenTriangles));
        Assert.DoesNotContain(document.Textures,
            static texture => texture.NativeChecksum == 0x7398654Bu);
        Assert.Contains(document.Textures,
            static texture => texture.NativeChecksum == 0x206C35DAu);

        var kevinGroup = Assert.Single(document.VisibilityGroups,
            static group => group.Source == ModelVisibilityGroupSource.TriggerRange
                            && group.Label.StartsWith("Kevin_00", StringComparison.Ordinal));
        Assert.False(kevinGroup.DefaultEnabled);
        Assert.False(kevinGroup.IsEnabled);
        Assert.StartsWith("psx.trg.", kevinGroup.Id, StringComparison.Ordinal);
        Assert.Contains("SetVisibilityByName", kevinGroup.SourceReference, StringComparison.Ordinal);

        var whatIfDocument = Parse(
            fixture.Source,
            "l8a4_g.psx",
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [kevinGroup.Id] = true
            });
        var selectedKevinGroup = Assert.Single(whatIfDocument.VisibilityGroups,
            group => group.Id == kevinGroup.Id);
        Assert.True(selectedKevinGroup.IsEnabled);
        var kevinTriangles = file.Objects
            .Where(static obj => obj.MeshIndex is >= 46 and <= 51)
            .Sum(obj => file.Meshes[obj.MeshIndex].Faces.Sum(
                static face => face.IsQuad ? 2 : 1));
        Assert.Equal(52, kevinTriangles);
        Assert.Equal(document.TriangleCount + kevinTriangles, whatIfDocument.TriangleCount);
        Assert.Contains(whatIfDocument.Textures,
            static texture => texture.NativeChecksum == 0x7398654Bu);

        var allAuthoredGeometry = Parse(
            fixture.Source,
            "l8a4_g.psx",
            document.VisibilityGroups.ToDictionary(
                static group => group.Id,
                static _ => true,
                StringComparer.Ordinal));
        Assert.Equal(
            authoredTriangleCount + placedObjectTriangleCount,
            allAuthoredGeometry.TriangleCount);
    }

    [Fact]
    public void L1a1_FromCdWad_ExposesInitiallyHiddenWhatIfSign()
    {
        using var fixture = OpenArchiveEntry("l1a1_g.psx");
        if (fixture == null)
            return;

        var document = Parse(fixture.Source, "l1a1_g.psx");
        var signGroup = Assert.Single(document.VisibilityGroups,
            static group => group.Source == ModelVisibilityGroupSource.TriggerRange
                            && group.Label.Contains("NYSign", StringComparison.OrdinalIgnoreCase));
        Assert.False(signGroup.DefaultEnabled);
        Assert.False(signGroup.IsEnabled);

        var enabledDocument = Parse(
            fixture.Source,
            "l1a1_g.psx",
            new Dictionary<string, bool>(StringComparer.Ordinal)
            {
                [signGroup.Id] = true
            });
        var enabledSignGroup = Assert.Single(enabledDocument.VisibilityGroups,
            group => group.Id == signGroup.Id);
        Assert.True(enabledSignGroup.IsEnabled);
        Assert.Equal(signGroup.Id, enabledSignGroup.Id);
        Assert.True(enabledDocument.TriangleCount > document.TriangleCount);
    }

    private static IEnumerable<int> PositiveJointIndices(ModelBoneInfluences influences)
    {
        if (influences.Weight0 > 0f) yield return influences.Joint0;
        if (influences.Weight1 > 0f) yield return influences.Joint1;
        if (influences.Weight2 > 0f) yield return influences.Joint2;
        if (influences.Weight3 > 0f) yield return influences.Joint3;
    }

    private static int CountTrianglesWithMatchingNormals(ModelPrimitive primitive)
    {
        var matching = 0;
        foreach (var triangle in primitive.Indices.Chunk(3))
        {
            var a = primitive.Vertices[triangle[0]];
            var b = primitive.Vertices[triangle[1]];
            var c = primitive.Vertices[triangle[2]];
            var geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position);
            if (geometric.LengthSquared() <= 1e-8f)
                continue;

            geometric = Vector3.Normalize(geometric);
            var averageNormal = Vector3.Normalize(a.Normal + b.Normal + c.Normal);
            if (Vector3.Dot(geometric, averageNormal) > 0f)
                matching++;
        }

        return matching;
    }

    private static int CountTrianglesWithMatchingNormals(RenderSubmesh submesh)
    {
        Assert.NotNull(submesh.Normals);
        var matching = 0;
        foreach (var triangle in submesh.Triangles.Chunk(3))
        {
            var a = ReadVector3(submesh.Positions, triangle[0]);
            var b = ReadVector3(submesh.Positions, triangle[1]);
            var c = ReadVector3(submesh.Positions, triangle[2]);
            var geometric = Vector3.Cross(b - a, c - a);
            if (geometric.LengthSquared() <= 1e-8f)
                continue;

            geometric = Vector3.Normalize(geometric);
            var averageNormal = Vector3.Normalize(
                ReadVector3(submesh.Normals!, triangle[0])
                + ReadVector3(submesh.Normals!, triangle[1])
                + ReadVector3(submesh.Normals!, triangle[2]));
            if (Vector3.Dot(geometric, averageNormal) > 0f)
                matching++;
        }

        return matching;
    }

    private static Vector3 ReadVector3(float[] values, int index)
    {
        var offset = index * 3;
        return new Vector3(values[offset], values[offset + 1], values[offset + 2]);
    }

    private static Vector3 Position(PsxMesh mesh, uint vertexIndex)
    {
        var vertex = mesh.Vertices[(int)vertexIndex];
        return new Vector3(vertex.X, vertex.Y, vertex.Z);
    }

    private static void AssertMeshTopology(
        PsxMeshFile file,
        int objectIndex,
        int expectedVertices,
        int expectedFaces)
    {
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex);
        Assert.InRange(meshIndex, 0, file.Meshes.Count - 1);
        Assert.Equal(expectedVertices, file.Meshes[meshIndex].Vertices.Count);
        Assert.Equal(expectedFaces, file.Meshes[meshIndex].Faces.Count);
    }

    private static int TriangleCount(PsxMeshFile file, int objectIndex)
    {
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(file, objectIndex);
        return file.Meshes[meshIndex].Faces.Sum(static face => face.IsQuad ? 2 : 1);
    }

    private static byte[] CreateSolidPng(int width, int height)
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(128, 128, 128, 255));
        using var stream = new MemoryStream();
        image.SaveAsPng(stream);
        return stream.ToArray();
    }

    private static ModelDocument Parse(
        ArchiveAssetSource source,
        string entryName,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx,
            VisibilityOverrides = visibilityOverrides
        });
    }

    private static ModelDocument ParseAnimated(
        ArchiveAssetSource source,
        string entryName,
        int boneCount)
    {
        var probe = AnimationDiscovery.FindForCharacter(
                source,
                boneCount,
                TestContext.Current.CancellationToken)
            .First(static item => item.MatchesSkeleton && item.Source is PsxAnimationSource);
        var animation = Assert.IsType<PsxAnimationSource>(probe.Source).Decode();

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx,
            PsxAnimationOptions = new PsxAnimationOptions(Fps: PsxAnimationBank.DefaultPreviewFps),
            PsxAnimationClips = [new PsxAnimationClip("appendage_motion", animation)]
        });
    }

    private ArchiveFixture? OpenArchiveEntry(
        string entryName,
        string buildName = BuildName,
        string archiveName = "CD.WAD")
    {
        var archivePath = paths.FindSampleFile(buildName, archiveName);
        Assert.SkipWhen(archivePath == null, $"{buildName} {archiveName} sample not available");
        if (archivePath == null)
            return null;

        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        Assert.NotNull(backend);
        if (backend == null)
            return null;

        var entry = backend.FindEntry(entryName);
        Assert.NotNull(entry);
        if (entry == null)
        {
            backend.FileSystem.Dispose();
            return null;
        }

        return new ArchiveFixture(backend, new ArchiveAssetSource(backend, entry));
    }

    private sealed class ArchiveFixture(
        ArchiveAssetBackend backend,
        ArchiveAssetSource source) : IDisposable
    {
        internal ArchiveAssetSource Source { get; } = source;

        public void Dispose()
        {
            backend.FileSystem.Dispose();
        }
    }
}
