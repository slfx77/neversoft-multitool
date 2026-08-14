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

    /// <summary>kind bit 0: the group's triangles are textured (slot at +0x02).</summary>
    private const int TexturedBit = 0x0001;

    /// <summary>
    ///     kind bit 10: the RSP geometry-mode <c>G_LIGHTING</c> carrier, ACTIVE
    ///     LOW — clear means lighting ON, so the pool's trailing bytes are a lit
    ///     NORMAL; set means lighting OFF and they are an RGBA colour.
    ///     <para>
    ///         Disassembled 2026-08-07 in all four ROMs. The display-list
    ///         emitter reduces it to a per-draw-entry boolean —
    ///         <c>lightingFlag = ((descriptor[+4] >> 10) &amp; 1) ^ 1</c> (THPS2
    ///         @0x800C5F28, THPS3 @0x800CA768, Spider-Man @0x800CFC18) — and the
    ///         consumer emits <c>G_GEOMETRYMODE</c> (0xD9) with set-mask
    ///         0x00020000 when it is true and the clear-mask word otherwise.
    ///         THPS1's older emitter tests the raw descriptor word in place
    ///         (@0x800AA2F4) with the same polarity. The polarity was read from
    ///         the EMIT, not assumed: the mask built via
    ///         <c>nor</c>/<c>and 0x00FFFFFF</c>/<c>or 0xD9000000</c> and written
    ///         with word1 = 0 is the CLEAR word. The embedded ucode string
    ///         "RSP Gfx ucode F3DEX.NoN fifo 2.08 ... 1999 Nintendo" fixes the
    ///         GBI as F3DEX2, so 0xD9 carries clear/set masks.
    ///     </para>
    ///     <para>
    ///         Independently refereed against the PS1 siblings, which share no
    ///         machinery with the N64 decode: of 718,411 triangles in bit-SET
    ///         groups, ZERO carry the PS1 engine-lit face flag 0x0004, while
    ///         55.7-90.3% of bit-CLEAR triangles do. A geometry-free byte-shape
    ///         oracle scores precision 100% / recall 96.3% over the 10,710 pools
    ///         where it is meaningful.
    ///     </para>
    ///     <para>
    ///         This REPLACES a byte-magnitude heuristic that admitted mid-grey
    ///         (69,69,69) and light-grey (177,177,177) alike, and so exported
    ///         5,522 nodes of authored colour as pure white — the reported
    ///         "geometry too bright". The true lit share is 1.7-7.6% of groups,
    ///         not the 12-38% of pools that heuristic claimed.
    ///     </para>
    /// </summary>
    private const int LightingDisabledBit = 0x0400;

    public readonly record struct N64Vertex(
        short X, short Y, short Z, short S, short T, byte R, byte G, byte B, byte A);

    public readonly record struct N64Corner(int Vertex, short S, short T, int MatrixIndex);

    /// <summary>
    ///     One decoded triangle. <c>Flags</c> is blob B's word, whose low half
    ///     is the PS1 DISC face flag word (see <see cref="Psx.PsxFaceFlags" />);
    ///     <c>TextureSlot</c> is the owning group's texture-dictionary slot
    ///     (0 = untextured).
    /// </summary>
    public readonly record struct N64Triangle(
        N64Corner C0, N64Corner C1, N64Corner C2, uint Flags, int MatrixIndex, int TextureSlot)
    {
        /// <summary>The PS1 face flag word carried in the low half of blob B's entry.</summary>
        public ushort FaceFlags => (ushort)Flags;

        public int V0 => C0.Vertex;
        public int V1 => C1.Vertex;
        public int V2 => C2.Vertex;
    }

    /// <summary>
    ///     One mesh node. <paramref name="HasNormals" /> reports whether the
    ///     pool's last four bytes hold a lit surface normal rather than an
    ///     authored vertex colour — F3DEX2 reuses the field for both and the
    ///     engine picks with G_LIGHTING, which the group descriptor carries
    ///     (see <see cref="LightingDisabledBit" />).
    ///     <para>
    ///         <paramref name="NodeIndex" /> is the mesh's position in the
    ///         record's root table, NOT its position in the returned list.
    ///         Nodes whose pool is empty or malformed are skipped, so the two
    ///         diverge (a THPS1 level drops 98 of 987) — and the shell pairs
    ///         placements to nodes POSITIONALLY, so using the list index
    ///         scatters every chunk after the first gap.
    ///     </para>
    /// </summary>
    public sealed record N64RenderMesh(
        IReadOnlyList<N64Vertex> Vertices,
        IReadOnlyList<N64Triangle> Triangles,
        float[] Bounds,
        bool HasNormals,
        int NodeIndex);

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

        for (var nodeIndex = 0; nodeIndex < root.Count; nodeIndex++)
        {
            var (start, end) = root[nodeIndex];
            var mesh = TryParseNode(data, start, end, nodeIndex);
            if (mesh != null)
                meshes.Add(mesh);
        }

        return meshes;
    }

    private static N64RenderMesh? TryParseNode(byte[] data, int start, int end, int nodeIndex)
    {
        var node = ReadTable(data, start, end);
        if (node is not { Count: 3 })
            return null;

        var bounds = ReadFloats(data, node[0].Start, node[0].End);
        var vertices = TryReadPool(data, node[2].Start, node[2].End, bounds);
        if (vertices == null)
            return null;

        var triangles = ReadGeometry(data, node[1].Start, node[1].End, vertices, out var lit);
        return new N64RenderMesh(vertices, triangles, bounds, lit, nodeIndex);
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

        if (BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(start + 4)) != 0)
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
    private static List<N64Triangle> ReadGeometry(
        byte[] data, int start, int end, IReadOnlyList<N64Vertex> vertices, out bool lit)
    {
        lit = false;
        var triangles = new List<N64Triangle>();
        var groups = ReadTable(data, start, end);
        if (groups == null)
            return triangles;

        var cache = new int[VertexCacheSize];
        Array.Fill(cache, -1);
        // Live ST per cache slot: seeded from the pool on G_VTX, rewritten by
        // G_MODIFYVTX. 41-57% of groups issue overrides, so ignoring them
        // mis-maps roughly half the corpus.
        var cacheS = new short[VertexCacheSize];
        var cacheT = new short[VertexCacheSize];
        var cacheMatrix = new int[VertexCacheSize];
        var cursor = 0;

        foreach (var (groupStart, groupEnd) in groups)
        {
            var group = ReadTable(data, groupStart, groupEnd);
            if (group is not { Count: 3 } || group[0].End - group[0].Start != DescriptorSize)
                continue;

            var kind = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(group[0].Start + 6));
            if ((kind & KindNonDisplayList) != 0)
                continue;

            // The lighting bit is per GROUP, but no node in the corpus mixes it
            // (0 of 41,905 across the four ROMs, including 7,000+ nodes with
            // more than one group), so one verdict per node is faithful and
            // keeps the pool decode - which is per node - coherent.
            if ((kind & LightingDisabledBit) == 0)
                lit = true;

            // Descriptor word 0 is a GLOBAL texture-dictionary slot index and
            // kind bit 0 is its enable flag. Both are decomp-verified:
            // ResolveGroupTextures loads word 0, passes it to TexMgr_Acquire
            // and overwrites the field in place with the resolved slot
            // pointer (THPS2 @0x800C7EF0). kind bit 0 <=> slot != 0 is an
            // exact biconditional over all 82,604 corpus descriptors, and a
            // PS1 cross-check found zero contradictions across 607 textures
            // shared between models - the index is model-independent.
            var textureSlot = (kind & TexturedBit) != 0
                ? (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(group[0].Start))
                : 0;
            var blobB = group[2];
            var flags = ReadFaceFlags(data, blobB.Start, blobB.End);
            ExpandDisplayList(
                data.AsSpan(group[1].Start, group[1].End - group[1].Start),
                cache, cacheS, cacheT, cacheMatrix, ref cursor, vertices, flags, textureSlot, triangles);
        }

        return triangles;
    }

    private static void ExpandDisplayList(
        ReadOnlySpan<byte> tokens,
        int[] cache,
        short[] cacheS,
        short[] cacheT,
        int[] cacheMatrix,
        ref int cursor,
        IReadOnlyList<N64Vertex> vertices,
        List<uint> faceFlags,
        int textureSlot,
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
                // Corner slots are packed low-to-high, but the resulting
                // triangle faces AWAY from the PS1 sibling's: pairing every
                // c_kart triangle by centroid against the PS1 export gives 175
                // reversed and 0 matching. Emitting (c2, c1, c0) restores the
                // engine's facing - without it, single-sided materials cull the
                // front and the model reads as hollow with inverted lighting.
                var s0 = (word >> 10) & 31;
                var s1 = (word >> 5) & 31;
                var s2 = word & 31;
                var v0 = cache[s0];
                var v1 = cache[s1];
                var v2 = cache[s2];
                var count = vertices.Count;
                if (v0 >= 0 && v1 >= 0 && v2 >= 0 && v0 < count && v1 < count && v2 < count)
                {
                    var flags = faceIndex < faceFlags.Count ? faceFlags[faceIndex] : 0u;
                    triangles.Add(new N64Triangle(
                        new N64Corner(v0, cacheS[s0], cacheT[s0], cacheMatrix[s0]),
                        new N64Corner(v1, cacheS[s1], cacheT[s1], cacheMatrix[s1]),
                        new N64Corner(v2, cacheS[s2], cacheT[s2], cacheMatrix[s2]),
                        flags, cacheMatrix[s0], textureSlot));
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
                    {
                        cache[v0 + k] = cursor;
                        // The matrix in force at LOAD time is the one the RSP
                        // applies to this vertex.
                        cacheMatrix[v0 + k] = matrixIndex;
                        // A freshly loaded slot starts at the pool's own ST.
                        if (cursor < vertices.Count)
                        {
                            cacheS[v0 + k] = vertices[cursor].S;
                            cacheT[v0 + k] = vertices[cursor].T;
                        }
                    }

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
            else if ((op & 0xE0) == 0x60) // G_MODIFYVTX(ST): rewrite a slot's UV
            {
                if (p + 4 >= tokens.Length)
                    break;
                var slot = op & 31;
                cacheS[slot] = BinaryPrimitives.ReadInt16BigEndian(tokens[(p + 1)..]);
                cacheT[slot] = BinaryPrimitives.ReadInt16BigEndian(tokens[(p + 3)..]);
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
        if (offset < 0 || limit < 0 || limit > data.Length || offset > limit || limit - offset < 8)
            return null;

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
        if (count == 0 || count > 65535)
            return null;

        var headerSize = 4 + 4 * ((int)count + 1);
        if (headerSize > limit - offset)
            return null;

        var offsets = new int[count + 1];
        for (var i = 0; i <= count; i++)
        {
            var relativeOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 4 + i * 4));
            if (relativeOffset > int.MaxValue)
                return null;
            offsets[i] = (int)relativeOffset;
        }

        if (offsets[0] != headerSize && offsets[0] != headerSize + 4)
            return null;

        for (var i = 0; i < count; i++)
        {
            if (offsets[i + 1] < offsets[i])
                return null;
        }

        if (offsets[count] > limit - offset)
            return null;

        var children = new List<(int, int)>((int)count);
        for (var i = 0; i < count; i++)
            children.Add((offset + offsets[i], offset + offsets[i + 1]));
        return children;
    }
}
