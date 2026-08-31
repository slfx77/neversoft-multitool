using System.Numerics;
using static NeversoftMultitool.Core.Formats.Mesh.XbxScene.NextGenSceneBinary;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Decodes the vertices of a later-revision next-gen scene mesh: positions,
///     authored normals, and the UV set the attribute block carries.
/// </summary>
internal static class NextGenVertexDecoder
{
    /// <summary>
    ///     Two vertex layouts, chosen by the record itself. A real pointer at +0x60
    ///     means a <c>CAFEBAB4</c> descriptor and a batch chain of stride-32 position
    ///     vertices. <c>0xFFFFFFFF</c> means the descriptor-less layout: the whole
    ///     vertex — position first — sits in the +0x40 block at a per-mesh stride the
    ///     record states as <c>+0x4C / vertexCount</c>, observed from 16 to 56 bytes.
    ///     The two are perfectly disjoint and no file mixes them; every <c>.mdl</c>
    ///     and <c>.scn</c> uses the second, which is why the level scenes carry no
    ///     descriptor at all.
    /// </summary>
    internal static XbxVertex[] Read(
        byte[] data, int scene, HashSet<int> descriptors, int record, int vertexCount)
    {
        var pointer = ReadUInt32(data, record + 0x60);
        if (pointer == uint.MaxValue)
            return ReadInline(data, scene, record, vertexCount);

        var descriptor = scene + (long)pointer;
        return descriptor > int.MaxValue || !descriptors.Contains((int)descriptor)
            ? []
            : ReadBatched(data, (int)descriptor, vertexCount);
    }

    private static XbxVertex[] ReadInline(byte[] data, int scene, int record, int vertexCount)
    {
        var block = ReadUInt32(data, record + 0x40);
        var byteSize = ReadUInt32(data, record + 0x4C);
        if (vertexCount <= 0 || block == uint.MaxValue || byteSize == 0 || byteSize == BadFood)
            return [];

        if (byteSize % (uint)vertexCount != 0)
            return [];

        var stride = (int)(byteSize / (uint)vertexCount);
        if (stride < 12)
            return [];

        var start = scene + (long)block + BlockHeaderSize;
        if (start < 0 || byteSize > data.Length - start)
            return [];

        var vertices = new XbxVertex[vertexCount];
        for (var i = 0; i < vertexCount; i++)
            vertices[i] = new XbxVertex
            {
                Position = ReadVec3(data, (int)start + stride * i),
                Color = Vector4.One
            };

        return vertices;
    }

    /// <summary>
    ///     Walks the batch chain. Each batch after the first is introduced by a
    ///     16-byte header whose leading big-endian word is its vertex count, and the
    ///     chain sums exactly to <c>vertexCount</c> (78+78+550+550 = 1256) — which is
    ///     why reading contiguously is wrong for every skinned mesh. The normal is
    ///     the file's own, packed at +0x10.
    /// </summary>
    private static XbxVertex[] ReadBatched(byte[] data, int descriptor, int vertexCount)
    {
        if (vertexCount <= 0 || descriptor < 0 || descriptor + BufferDescriptorSize > data.Length)
            return [];

        var (countOffset, dataOffset) = DescriptorShape(data, descriptor);
        var vertices = new List<XbxVertex>(vertexCount);
        var cursor = descriptor + dataOffset;
        var batch = (int)ReadUInt32(data, descriptor + countOffset);

        while (vertices.Count < vertexCount && IsReadableBatch(data, cursor, batch))
        {
            var take = Math.Min(batch, vertexCount - vertices.Count);
            for (var i = 0; i < take; i++)
            {
                var vertex = cursor + VertexStride * i;
                vertices.Add(new XbxVertex
                {
                    Position = ReadVec3(data, vertex),
                    Normal = UnpackUnitVector(ReadUInt32(data, vertex + 0x10)),
                    HasNormal = true,
                    Color = Vector4.One
                });
            }

            cursor += batch * VertexStride;
            if (vertices.Count >= vertexCount || cursor + BatchHeaderSize > data.Length)
                break;

            batch = (int)ReadUInt32(data, cursor);
            cursor += BatchHeaderSize;
        }

        return vertices.Count == vertexCount ? vertices.ToArray() : [];
    }

    private static bool IsReadableBatch(byte[] data, int cursor, int batch)
    {
        if (batch <= 0 || batch > 0xFFFF || cursor < 0)
            return false;

        return (long)batch * VertexStride <= data.Length - cursor;
    }

    /// <summary>
    ///     The attribute block is a per-vertex stream in the +0x40 block, and the
    ///     record states everything needed to read it: the pointer at +0x40, the byte
    ///     size at +0x4C, and — critically — the STRIDE at +0x5A.
    ///     <para>
    ///         A fixed 16-byte entry is wrong: that holds for only 3,959 of 6,594
    ///         meshes (60%), whereas <c>stride * vertexCount == byteSize</c> holds for
    ///         6,565 (99.6%, the exceptions being stub records). Strides run 4 to 32
    ///         because a mesh carries one to seven UV sets. The layout is declared
    ///         too, in a merged address space where the 32-byte position stream
    ///         occupies 0..31: byte +0x1A is where UV set 0 begins and byte +0x1B is
    ///         the colour's offset, so the first UV pair sits at <c>(+0x1A) - 32</c>
    ///         into each entry rather than at a fixed +4.
    ///     </para>
    ///     <para>
    ///         This runs only for the descriptor path. A descriptor-less mesh has
    ///         already consumed the +0x40 block as its interleaved vertex stream, and
    ///         its UV placement within that stride is not established.
    ///     </para>
    /// </summary>
    internal static void ApplyAttributes(
        byte[] data, int scene, int record, XbxVertex[] vertices, byte[]? vram)
    {
        if (ReadUInt32(data, record + 0x60) == uint.MaxValue)
            return;

        var pointer = ReadUInt32(data, record + 0x40);
        var size = ReadUInt32(data, record + 0x4C);
        if (pointer == uint.MaxValue || size == 0 || size == BadFood)
            return;

        int stride = data[record + 0x5A];
        var uvOffset = data[record + 0x1A] - PositionStreamSize;
        if (stride <= 0 || uvOffset < 0 || uvOffset + 4 > stride)
            return;

        if ((long)stride * vertices.Length != size)
            return;

        var (buffer, start) = ResolveBlock(data, scene, pointer, vram);
        if (start < 0 || size > buffer.Length - start)
            return;

        for (var i = 0; i < vertices.Length; i++)
        {
            var o = (int)start + stride * i + uvOffset;
            vertices[i].TexCoord = new Vector2(
                HalfToSingle(ReadUInt16(buffer, o)),
                HalfToSingle(ReadUInt16(buffer, o + 2)));
        }
    }

    /// <summary>
    ///     Area-weighted vertex normals derived from the triangles, for the
    ///     descriptor-less layout only — there the vertex stride runs from 16 to 56
    ///     bytes and nothing but the position at +0x00 is established, so there is no
    ///     authored normal to read. Meshes on the descriptor path already carry the
    ///     file's own normals and are left alone.
    ///     <para>
    ///         Some normal is required either way: without one every vertex would
    ///         reach the glTF writer with the same substituted normal, so seam
    ///         vertices — which share a position and are distinguished in-game only by
    ///         bone weights we do not export — would merge and take their triangles
    ///         with them as degenerates.
    ///     </para>
    /// </summary>
    internal static void ComputeNormals(XbxVertex[] vertices, ushort[] indices)
    {
        if (vertices.Length == 0 || indices.Length < 3 || vertices[0].HasNormal)
            return;

        var sums = new Vector3[vertices.Length];
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (a >= vertices.Length || b >= vertices.Length || c >= vertices.Length)
                continue;

            // Unnormalized cross product weights each face by twice its area.
            var face = Vector3.Cross(
                vertices[b].Position - vertices[a].Position,
                vertices[c].Position - vertices[a].Position);
            sums[a] += face;
            sums[b] += face;
            sums[c] += face;
        }

        for (var i = 0; i < vertices.Length; i++)
        {
            if (sums[i].LengthSquared() <= 0)
                continue;

            vertices[i].Normal = Vector3.Normalize(sums[i]);
            vertices[i].HasNormal = true;
        }
    }
}
