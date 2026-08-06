using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Decodes an N64 render-bank record (<c>group2/NNN.bin</c>) — the
///     geometry container of the Edge of Reality ports, RE'd 2026-08-06.
///     It is NOT the "bit-packed vertex codec" the docs long assumed: it is a
///     <b>packed F3DEX2 display list over a plain vertex array</b>.
///
///     <para>Record layout (BE throughout; every table's offsets are relative
///     to that table's own start):</para>
///     <code>
///     record   -> table of MESH NODES
///     node     -> 3 children: [0] float bounds, [1] geometry, [2] vertex pool
///     pool     -> u32 count, u32 0, then count*16 bytes
///     geometry -> table of GROUPS
///     group    -> 3 children: [0] 12-byte descriptor, [1] blob A, [2] blob B
///     </code>
///
///     <para>The pool holds stock 16-byte F3DEX2 vertices
///     (<c>s16 x,y,z; u16 flag; s16 s,t; u8 r,g,b,a</c>) — stored
///     <b>byte-plane transposed</b> (16 planes of <c>count</c> bytes) in
///     THPS2/THPS3/Spider-Man but <b>plain</b> in THPS1. The layout correlates
///     with the bounds size (32 B plain / 56 B transposed) but is decided here
///     by testing both against the record's own float bounds, which is exact
///     rather than merely correlated.</para>
///
///     <para>Blob A is a two-byte token stream that expands to a display list;
///     nothing in it carries coordinates. Blob B carries one u32 per triangle
///     whose low half is the PS1 per-face flag word.</para>
/// </summary>
public static class N64RenderBankFile
{
    private const int VertexStride = 16;
    private const int VertexCacheSize = 32;
    private const int DescriptorSize = 12;

    /// <summary>Marks a group whose blob A is not a packed display list (2.5% of the corpus).</summary>
    private const int KindNonDisplayList = 0x8000;

    public readonly record struct N64Vertex(
        short X, short Y, short Z, short S, short T, byte R, byte G, byte B, byte A);

    /// <summary>
    ///     One decoded triangle. <paramref name="Flags" /> is blob B's word,
    ///     whose low half is the PS1 DISC face flag word (see
    ///     <see cref="Psx.PsxFaceFlags" />); <paramref name="TextureId" /> is
    ///     the owning group's descriptor id.
    /// </summary>
    public readonly record struct N64Triangle(
        int V0, int V1, int V2, uint Flags, int MatrixIndex, uint TextureId)
    {
        /// <summary>The PS1 face flag word carried in the low half of blob B's entry.</summary>
        public ushort FaceFlags => (ushort)Flags;
    }

    public sealed record N64RenderMesh(
        IReadOnlyList<N64Vertex> Vertices,
        IReadOnlyList<N64Triangle> Triangles,
        float[] Bounds);

    /// <summary>
    ///     Parses every mesh node in a record. Returns an empty list when the
    ///     buffer is not a render-bank record (8-byte stub slots, for example).
    /// </summary>
    public static IReadOnlyList<N64RenderMesh> Parse(byte[] data)
    {
        var meshes = new List<N64RenderMesh>();
        var root = ReadTable(data, 0, data.Length);
        if (root == null)
            return meshes;

        foreach (var (start, end) in root)
        {
            var mesh = TryParseNode(data, start, end);
            if (mesh != null)
                meshes.Add(mesh);
        }

        return meshes;
    }

    private static N64RenderMesh? TryParseNode(byte[] data, int start, int end)
    {
        var node = ReadTable(data, start, end);
        if (node is not { Count: 3 })
            return null;

        var bounds = ReadFloats(data, node[0].Start, node[0].End);
        var vertices = TryReadPool(data, node[2].Start, node[2].End, bounds);
        if (vertices == null)
            return null;

        var triangles = ReadGeometry(data, node[1].Start, node[1].End, vertices.Count);
        return new N64RenderMesh(vertices, triangles, bounds);
    }

    /// <summary>
    ///     Reads the vertex pool, choosing between the plain and byte-plane
    ///     transposed layouts by testing each against the node's own float
    ///     bounds. The wrong layout scrambles coordinates, so its extents miss
    ///     the bounds by a wide margin — the test is decisive, never a tie in
    ///     the measured corpus.
    /// </summary>
    private static List<N64Vertex>? TryReadPool(byte[] data, int start, int end, float[] bounds)
    {
        if (end - start < 8)
            return null;

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));
        if (count == 0 || count > (end - start - 8) / VertexStride)
            return null;

        var body = data.AsSpan(start + 8, (int)count * VertexStride);
        var plain = DecodeVertices(body, (int)count, transposed: false);
        var transposed = DecodeVertices(body, (int)count, transposed: true);

        if (bounds.Length < 6)
            return transposed;

        return BoundsError(transposed, bounds) <= BoundsError(plain, bounds) ? transposed : plain;
    }

    /// <summary>
    ///     Squared mismatch between a decoded vertex set's extents and the
    ///     node's authored bounds. Lower is better; the correct layout scores
    ///     near zero.
    /// </summary>
    private static double BoundsError(List<N64Vertex> vertices, float[] bounds)
    {
        if (vertices.Count == 0)
            return double.MaxValue;

        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var v in vertices)
        {
            minX = Math.Min(minX, v.X);
            minY = Math.Min(minY, v.Y);
            minZ = Math.Min(minZ, v.Z);
            maxX = Math.Max(maxX, v.X);
            maxY = Math.Max(maxY, v.Y);
            maxZ = Math.Max(maxZ, v.Z);
        }

        return Sq(minX - bounds[0]) + Sq(minY - bounds[1]) + Sq(minZ - bounds[2])
               + Sq(maxX - bounds[3]) + Sq(maxY - bounds[4]) + Sq(maxZ - bounds[5]);

        static double Sq(double v) => v * v;
    }

    private static List<N64Vertex> DecodeVertices(ReadOnlySpan<byte> body, int count, bool transposed)
    {
        var vertices = new List<N64Vertex>(count);
        Span<byte> record = stackalloc byte[VertexStride];
        for (var i = 0; i < count; i++)
        {
            if (transposed)
            {
                // 16 planes of `count` bytes: element i's byte k is at plane k, index i.
                for (var k = 0; k < VertexStride; k++)
                    record[k] = body[k * count + i];
            }
            else
            {
                body.Slice(i * VertexStride, VertexStride).CopyTo(record);
            }

            vertices.Add(new N64Vertex(
                BinaryPrimitives.ReadInt16BigEndian(record),
                BinaryPrimitives.ReadInt16BigEndian(record[2..]),
                BinaryPrimitives.ReadInt16BigEndian(record[4..]),
                // record[6..8] is the F3DEX2 flag field: zero in every corpus vertex.
                BinaryPrimitives.ReadInt16BigEndian(record[8..]),
                BinaryPrimitives.ReadInt16BigEndian(record[10..]),
                record[12], record[13], record[14], record[15]));
        }

        return vertices;
    }

    /// <summary>
    ///     Walks the node's groups, expanding each packed display list. One
    ///     pool cursor advances across every group in the node: a G_VTX token
    ///     takes the next n pool entries into cache slots [v0, v0+n).
    /// </summary>
    private static List<N64Triangle> ReadGeometry(byte[] data, int start, int end, int vertexCount)
    {
        var triangles = new List<N64Triangle>();
        var groups = ReadTable(data, start, end);
        if (groups == null)
            return triangles;

        var cache = new int[VertexCacheSize];
        Array.Fill(cache, -1);
        var cursor = 0;

        foreach (var (groupStart, groupEnd) in groups)
        {
            var group = ReadTable(data, groupStart, groupEnd);
            if (group is not { Count: 3 } || group[0].End - group[0].Start != DescriptorSize)
                continue;

            var kind = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(group[0].Start + 6));
            if ((kind & KindNonDisplayList) != 0)
                continue;

            // Descriptor word 0 selects the group's texture (see the class doc).
            var textureId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(group[0].Start));
            var blobB = group[2];
            var flags = ReadFaceFlags(data, blobB.Start, blobB.End);
            ExpandDisplayList(
                data.AsSpan(group[1].Start, group[1].End - group[1].Start),
                cache, ref cursor, vertexCount, flags, textureId, triangles);
        }

        return triangles;
    }

    private static void ExpandDisplayList(
        ReadOnlySpan<byte> tokens,
        int[] cache,
        ref int cursor,
        int vertexCount,
        List<uint> faceFlags,
        uint textureId,
        List<N64Triangle> triangles)
    {
        var matrixIndex = 0;
        var faceIndex = 0;
        var p = 0;
        while (p < tokens.Length)
        {
            var op = tokens[p];
            if (op == 0x00) // G_ENDDL
                break;

            if ((op & 0x80) != 0) // triangle
            {
                if (p + 1 >= tokens.Length)
                    break;
                var word = (op << 8) | tokens[p + 1];
                // Corner order matching the PS1 sibling exactly (703/703 in the
                // cross-check); the GBI word the engine emits is its reverse.
                var v0 = cache[word & 31];
                var v1 = cache[(word >> 5) & 31];
                var v2 = cache[(word >> 10) & 31];
                if (v0 >= 0 && v1 >= 0 && v2 >= 0 && v0 < vertexCount && v1 < vertexCount && v2 < vertexCount)
                {
                    var flags = faceIndex < faceFlags.Count ? faceFlags[faceIndex] : 0u;
                    triangles.Add(new N64Triangle(v0, v1, v2, flags, matrixIndex, textureId));
                }

                faceIndex++;
                p += 2;
            }
            else if ((op & 0xE0) == 0x20) // G_VTX
            {
                if (p + 1 >= tokens.Length)
                    break;
                var word = (op << 8) | tokens[p + 1];
                var n = word & 31;
                if (n == 0)
                    n = 32;
                var v0 = (word >> 5) & 31;
                for (var k = 0; k < n; k++)
                {
                    if (v0 + k < VertexCacheSize)
                        cache[v0 + k] = cursor;
                    cursor++;
                }

                p += 2;
            }
            else if ((op & 0xE0) == 0x40) // G_MTX
            {
                if (p + 1 >= tokens.Length)
                    break;
                matrixIndex = tokens[p + 1];
                p += 2;
            }
            else if ((op & 0xE0) == 0x60) // G_MODIFYVTX(ST) — per-slot UV override
            {
                p += 5;
            }
            else
            {
                break;
            }
        }
    }

    /// <summary>Blob B: u32 count then one u32 per triangle (low half = the PS1 face flags).</summary>
    private static List<uint> ReadFaceFlags(byte[] data, int start, int end)
    {
        var flags = new List<uint>();
        if (end - start < 4)
            return flags;

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start));
        if (count > (end - start - 4) / 4)
            return flags;

        for (var i = 0; i < count; i++)
            flags.Add(BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 4 + i * 4)));

        return flags;
    }

    private static float[] ReadFloats(byte[] data, int start, int end)
    {
        var count = (end - start) / 4;
        var values = new float[count];
        for (var i = 0; i < count; i++)
            values[i] = BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(start + i * 4));
        return values;
    }

    /// <summary>
    ///     Reads the recursive table shared by the whole N64 asset format:
    ///     BE u32 count, then count+1 non-decreasing offsets RELATIVE TO THE
    ///     TABLE'S OWN START, the first equal to the header size (some tables
    ///     carry an extra alignment word).
    /// </summary>
    private static List<(int Start, int End)>? ReadTable(byte[] data, int offset, int limit)
    {
        if (offset < 0 || offset + 8 > limit || limit > data.Length)
            return null;

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        if (count == 0 || count > 65535)
            return null;

        var headerSize = 4 + 4 * ((int)count + 1);
        if (offset + headerSize > limit)
            return null;

        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
            offsets[i] = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4 + i * 4));

        if (offsets[0] != headerSize && offsets[0] != headerSize + 4)
            return null;

        for (var i = 0; i < count; i++)
        {
            if (offsets[i + 1] < offsets[i])
                return null;
        }

        if (offset + offsets[count] > limit)
            return null;

        var children = new List<(int, int)>((int)count);
        for (var i = 0; i < count; i++)
            children.Add((offset + offsets[i], offset + offsets[i + 1]));
        return children;
    }
}
