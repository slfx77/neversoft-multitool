using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Psx;

public sealed class PsxStitchCorpusRegressionTests(TestPaths paths)
{
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2000-9-19, PSX - Final)";
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2001-10-3, PSX - Final)";
    private const string Thps4Build = "Tony Hawk's Pro Skater 4 (2002-9-28, PSX - Final)";

    [CorpusFact]
    public void Lasek2Head_Thps3Vertex51_IsAnAuthoredOutlierBetweenMatchingPeers()
    {
        var thps2 = ParseLasek(Thps2Build);
        var thps3 = ParseLasek(Thps3Build);
        var thps4 = ParseLasek(Thps4Build);

        var (thps2Head, thps2HeadIndex) = FindHead(thps2);
        var (thps3Head, thps3HeadIndex) = FindHead(thps3);
        var (thps4Head, thps4HeadIndex) = FindHead(thps4);

        // All three games use the same 64-vertex / 87-face head topology. THPS2 and
        // THPS4 also carry the exact same ordinary chin vertex at slot 51. Only the
        // intervening THPS3 asset replaces that record with a type-2 stitch reference.
        AssertMatchingTopology(thps2Head, thps3Head);
        AssertMatchingTopology(thps3Head, thps4Head);
        AssertRawVertex(thps2Head.Vertices[51], -2, 89, -224, 0);
        AssertRawVertex(thps4Head.Vertices[51], -2, 89, -224, 0);
        AssertRawVertex(thps3Head.Vertices[51], 0, 39, 0, PsxMeshSemantics.StitchedReferenceType);

        var thps2Chin = PsxCharacterMeshResolver.ResolveVertex(thps2, thps2HeadIndex, 51);
        var thps3Chin = PsxCharacterMeshResolver.ResolveVertex(thps3, thps3HeadIndex, 51);
        var thps4Chin = PsxCharacterMeshResolver.ResolveVertex(thps4, thps4HeadIndex, 51);
        Assert.False(thps2Chin.UsedAttachment);
        Assert.False(thps4Chin.UsedAttachment);
        AssertVectorNear(thps2Chin.WorldPosition, thps4Chin.WorldPosition, 0.0001f);

        // The THPS3 executable's loader interprets raw Y=39 as global stitch source
        // 39, exactly as the parser does. That source is stomach mesh 10 / vertex 3,
        // about 23 units from the corrected THPS4 peer, and stretches all five faces
        // incident on the chin record.
        Assert.True(thps3Chin.UsedAttachment);
        Assert.True(thps3Chin.AttachmentResolved);
        Assert.Equal(10, thps3Chin.SourceMeshIndex);
        Assert.Equal(3, thps3Chin.SourceVertexIndex);
        Assert.True(Vector3.Distance(thps3Chin.WorldPosition, thps4Chin.WorldPosition) > 20f);
        Assert.Equal(5, thps3Head.Faces.Count(face => EnumerateIndices(face).Contains(51u)));

        // The head's other four authored references all resolve to its direct parent,
        // chest mesh 11. Vertex 51 alone targets the grandparent stomach. An alternate
        // file-wide index base therefore cannot repair it without breaking the four
        // valid, symmetric seam references.
        var parentMeshIndex = thps3.Objects[thps3HeadIndex].ParentIndex;
        var grandparentMeshIndex = thps3.Objects[parentMeshIndex].ParentIndex;
        Assert.Equal(11, parentMeshIndex);
        Assert.Equal(10, grandparentMeshIndex);
        Assert.Equal(grandparentMeshIndex, thps3Chin.SourceMeshIndex);

        var validReferences = new Dictionary<int, short>
        {
            [18] = 53,
            [19] = 64,
            [29] = 59,
            [30] = 63
        };
        Assert.Equal(5, thps3Head.Vertices.Count(vertex =>
            PsxMeshSemantics.IsExactStitchedReference(vertex.Type)));
        foreach (var (vertexIndex, targetIndex) in validReferences)
        {
            Assert.Equal(targetIndex, thps3Head.Vertices[vertexIndex].RawY);
            var resolved = PsxCharacterMeshResolver.ResolveVertex(
                thps3, thps3HeadIndex, (uint)vertexIndex);
            Assert.True(resolved.AttachmentResolved);
            Assert.Equal(parentMeshIndex, resolved.SourceMeshIndex);
        }
    }

    private PsxMeshFile ParseLasek(string build)
    {
        var path = paths.FindSampleFile(build, "lasek2.psx");
        Assert.SkipWhen(path is null, $"lasek2.psx not found in {build}");
        return PsxMeshFile.Parse(path!)!;
    }

    private static (PsxMesh Mesh, int Index) FindHead(PsxMeshFile file)
    {
        return file.Meshes
            .Select((mesh, index) => (Mesh: mesh, Index: index))
            .Single(entry => entry.Mesh.Vertices.Count == 64 && entry.Mesh.Faces.Count == 87);
    }

    private static void AssertMatchingTopology(PsxMesh expected, PsxMesh actual)
    {
        Assert.Equal(expected.Vertices.Count, actual.Vertices.Count);
        Assert.Equal(expected.Faces.Count, actual.Faces.Count);
        for (var faceIndex = 0; faceIndex < expected.Faces.Count; faceIndex++)
        {
            var expectedFace = expected.Faces[faceIndex];
            var actualFace = actual.Faces[faceIndex];
            Assert.Equal(expectedFace.IsQuad, actualFace.IsQuad);
            Assert.Equal(EnumerateIndices(expectedFace), EnumerateIndices(actualFace));
        }
    }

    private static uint[] EnumerateIndices(PsxFace face)
    {
        return face.IsQuad
            ? [face.Index0, face.Index1, face.Index2, face.Index3]
            : [face.Index0, face.Index1, face.Index2];
    }

    private static void AssertRawVertex(
        PsxVertex vertex, short rawX, short rawY, short rawZ, ushort type)
    {
        Assert.Equal(rawX, vertex.RawX);
        Assert.Equal(rawY, vertex.RawY);
        Assert.Equal(rawZ, vertex.RawZ);
        Assert.Equal(type, vertex.Type);
    }

    private static void AssertVectorNear(Vector3 expected, Vector3 actual, float epsilon)
    {
        Assert.True(Vector3.Distance(expected, actual) <= epsilon,
            $"Expected {expected}, actual {actual}");
    }
}
