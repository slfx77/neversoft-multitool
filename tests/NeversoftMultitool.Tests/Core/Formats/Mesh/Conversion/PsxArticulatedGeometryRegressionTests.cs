using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.QbKey;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Conversion;

public sealed class PsxArticulatedGeometryRegressionTests(TestPaths paths)
{
    private const string BuildName = "Spider-Man (2000-9-1, PSX - Final)";
    private static readonly int[] CarnageHandObjects = [13, 14, 17, 18];

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
            // them invisible; they reference the two otherwise-unused 64x64
            // library slots. The 22 drawable faces all use slot zero.
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
        }
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

        var document = Parse(fixture.Source, "l8a4_g.psx");
        // With a single unambiguous restart, apply the whole authored default
        // state: Kevin_ plus the A/B/C/D On/Glowing alternates start hidden.
        Assert.Equal(452, document.TriangleCount);
        Assert.DoesNotContain(document.Textures,
            static texture => texture.NativeChecksum == 0x7398654Bu);
        Assert.Contains(document.Textures,
            static texture => texture.NativeChecksum == 0x206C35DAu);
    }

    private static IEnumerable<int> PositiveJointIndices(ModelBoneInfluences influences)
    {
        if (influences.Weight0 > 0f) yield return influences.Joint0;
        if (influences.Weight1 > 0f) yield return influences.Joint1;
        if (influences.Weight2 > 0f) yield return influences.Joint2;
        if (influences.Weight3 > 0f) yield return influences.Joint3;
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

    private static ModelDocument Parse(ArchiveAssetSource source, string entryName)
    {
        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entryName,
            OutputStem = Path.GetFileNameWithoutExtension(entryName),
            SourceKind = ModelSourceKind.Psx
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

    private ArchiveFixture? OpenArchiveEntry(string entryName)
    {
        var wadPath = paths.FindSampleFile(BuildName, "CD.WAD");
        Assert.SkipWhen(wadPath == null, "Spider-Man PSX CD.WAD sample not available");
        if (wadPath == null)
            return null;

        var backend = ArchiveAssetBackend.TryOpen(wadPath);
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
