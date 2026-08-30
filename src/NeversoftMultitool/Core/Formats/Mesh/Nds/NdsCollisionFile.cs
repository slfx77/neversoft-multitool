using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>One collision face: three vertices, a volume tag and a surface word.</summary>
/// <param name="VolumeKind">
///     Low nibble of the tag word. Zero for an ordinary collision face; non-zero marks
///     a member of a gameplay volume.
/// </param>
/// <param name="VolumeId">
///     The tag word's top 12 bits. Constant across a volume's faces (1,477 of 1,488
///     components), so it identifies the volume — but the values are not dense
///     ordinals, so what they are a handle INTO is unresolved.
/// </param>
/// <param name="Surface">
///     The raw surface word. Its low byte is a small terrain code and its high byte a
///     group id; only the low-nibble read is code-proven, and only on Sk8land — see
///     <see cref="NdsCollisionFile" />.
/// </param>
public readonly record struct NdsCollisionFace(
    int V0, int V1, int V2, int VolumeKind, int VolumeId, ushort Surface)
{
    /// <summary>True when the face belongs to a gameplay volume rather than the surface.</summary>
    public bool IsVolume => VolumeKind != 0;
}

/// <summary>
///     One link of the edge network: a segment plus its neighbours in the chain.
/// </summary>
public readonly record struct NdsCollisionEdge(
    int V0, int V1, int Next, int Previous, int Kind, int Flags, ushort Parameter)
{
    public const int NoNeighbour = 0xFFFF;

    public bool IsChainHead => Previous == NoNeighbour;
    public bool IsChainTail => Next == NoNeighbour;
}

/// <summary>
///     A Vicarious Visions DS level's collision world — the <c>.lwc</c> beside each
///     level's <c>.prp</c>.
///
///     It holds three things over one shared vertex array: a triangle collision
///     surface, a doubly-linked EDGE NETWORK authored separately from that surface,
///     and a small class of triangles tagged into closed gameplay volumes.
///
///     <code>
///     +0x00 char[3] 'LWC'
///     +0x03 u8      version        1 Sk8land, 3 Downhill Jam, 4 Proving Ground
///     +0x04 u32     vertexCount    +0x08 u32 vertexOffset   (== headerSize)
///     +0x0C u32     triangleCount  +0x10 u32 triangleOffset
///     +0x14 u32     edgeCount      +0x18 u32 edgeOffset
///     +0x1C u32     accelOffset[]  the rest of the header; last section ends at EOF
///     </code>
///
///     The layout is self-describing and that is the parse gate: each section's stored
///     offset is reproduced by <c>align4(previous + count * stride)</c> with strides
///     6, 10 and 12, in all 23 shipped files. Three strides landing on three stored
///     offsets 23 times over is not a fit — and the ARM9 states them independently
///     (<c>LwcFile::Init</c> at <c>0x02047468</c> rebases exactly these fields, and the
///     element accessors at <c>0x02047A70</c>/<c>0x02047A80</c> spell strides 10 and 6).
///
///     Vertices are <c>s16</c> at <b>1/32</b> of a world unit, Z-up, the same space as
///     the level's render geometry — chosen against the render box over all 20 levels
///     rather than tuned: the own-level box wins at /32 for 15 of them and for at most
///     6 at any other power of two.
///
///     <b>What is deliberately not claimed.</b> The terrain code's WIDTH is
///     version-dependent: the Sk8land accessor reads a nibble, but Downhill Jam's
///     values run to 21, so the raw word is exposed rather than a decoded field. The
///     edge network's role as grind rails is an inference from its shape (a chained
///     1..6-typed polyline laid over the surface, sharing only 2,187 of 21,177 edges
///     with the mesh) — the structure is proven, the purpose is not. And the volume id
///     is not an ordinal, so what it indexes stays open.
/// </summary>
public sealed class NdsCollisionFile
{
    private const int FixedHeaderWords = 6;
    private const int VertexStride = 6;
    private const int FaceStride = 10;
    private const int EdgeStride = 12;

    /// <summary>World units per stored vertex unit.</summary>
    public const float VertexScale = 32f;

    private NdsCollisionFile(
        int version, Vector3[] vertices, NdsCollisionFace[] faces, NdsCollisionEdge[] edges)
    {
        Version = version;
        Vertices = vertices;
        Faces = faces;
        Edges = edges;
    }

    /// <summary>1 = Sk8land, 3 = Downhill Jam, 4 = Proving Ground.</summary>
    public int Version { get; }

    /// <summary>Shared by the faces and the edges, in world units.</summary>
    public IReadOnlyList<Vector3> Vertices { get; }

    public IReadOnlyList<NdsCollisionFace> Faces { get; }

    /// <summary>The chained edge network laid over the surface.</summary>
    public IReadOnlyList<NdsCollisionEdge> Edges { get; }

    public static bool IsCollisionWorld(ReadOnlySpan<byte> data) => TryParse(data, out _);

    public static bool TryParse(
        ReadOnlySpan<byte> data, [NotNullWhen(true)] out NdsCollisionFile? file)
    {
        file = null;
        if (data.Length < 0x1C
            || data[0] != (byte)'L' || data[1] != (byte)'W' || data[2] != (byte)'C')
        {
            return false;
        }

        var vertexCount = Read(data, 1);
        var vertexOffset = Read(data, 2);
        var faceCount = Read(data, 3);
        var faceOffset = Read(data, 4);
        var edgeCount = Read(data, 5);
        var edgeOffset = Read(data, 6);

        // The header ends where the vertices begin, which is how many acceleration
        // sections there are — the file states its own header size.
        if (vertexOffset < (FixedHeaderWords + 1) * 4 || vertexOffset % 4 != 0
            || vertexOffset > data.Length)
        {
            return false;
        }

        if (vertexCount <= 0 || faceCount < 0 || edgeCount < 0
            || vertexCount > 1 << 20 || faceCount > 1 << 20 || edgeCount > 1 << 20)
        {
            return false;
        }

        // Every section offset is derivable, and reproducing the stored one is the gate.
        if (faceOffset != Align4(vertexOffset + vertexCount * VertexStride)
            || edgeOffset != Align4(faceOffset + faceCount * FaceStride)
            || Align4(edgeOffset + edgeCount * EdgeStride) > data.Length)
        {
            return false;
        }

        var vertices = new Vector3[vertexCount];
        for (var i = 0; i < vertexCount; i++)
        {
            var at = vertexOffset + i * VertexStride;
            vertices[i] = new Vector3(
                BinaryPrimitives.ReadInt16LittleEndian(data[at..]) / VertexScale,
                BinaryPrimitives.ReadInt16LittleEndian(data[(at + 2)..]) / VertexScale,
                BinaryPrimitives.ReadInt16LittleEndian(data[(at + 4)..]) / VertexScale);
        }

        var faces = new NdsCollisionFace[faceCount];
        for (var i = 0; i < faceCount; i++)
        {
            var at = faceOffset + i * FaceStride;
            int v0 = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            int v1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
            int v2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
            if (v0 >= vertexCount || v1 >= vertexCount || v2 >= vertexCount)
                return false;

            var tag = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..]);
            faces[i] = new NdsCollisionFace(
                v0, v1, v2, tag & 0xF, tag >> 4,
                BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 8)..]));
        }

        var edges = new NdsCollisionEdge[edgeCount];
        for (var i = 0; i < edgeCount; i++)
        {
            var at = edgeOffset + i * EdgeStride;
            int v0 = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            int v1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 2)..]);
            int next = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 4)..]);
            int previous = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 6)..]);
            if (v0 >= vertexCount || v1 >= vertexCount)
                return false;
            if (next != NdsCollisionEdge.NoNeighbour && next >= edgeCount)
                return false;
            if (previous != NdsCollisionEdge.NoNeighbour && previous >= edgeCount)
                return false;

            var kind = BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 8)..]);
            edges[i] = new NdsCollisionEdge(
                v0, v1, next, previous, kind & 0xF, kind >> 4,
                BinaryPrimitives.ReadUInt16LittleEndian(data[(at + 10)..]));
        }

        file = new NdsCollisionFile(data[3], vertices, faces, edges);
        return true;
    }

    private static int Read(ReadOnlySpan<byte> data, int word) =>
        (int)BinaryPrimitives.ReadUInt32LittleEndian(data[(word * 4)..]);

    private static int Align4(int value) => (value + 3) & ~3;
}
