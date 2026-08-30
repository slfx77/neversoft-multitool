using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Project 8 and Proving Ground scene geometry (derived 2026-08-28). Same
///     <c>FAAABACA</c> container and same 128-byte sMesh record as
///     <see cref="NextGenSceneFile" />, but a later revision that moves nearly
///     everything else. The two games are ONE format: a cutscene shipping in both
///     builds has byte-identical mesh records apart from four unresolved pointer
///     slots, and identical vertex bytes apart from one word.
///     <para>
///         <b>The mesh table is LOCATED, not trusted.</b> Fitting CScene field offsets
///         to one file and sweeping scored 821/3380 — the CScene layout is not stable
///         across file kinds. The buffers carry a 16-byte <c>CAFEBAB4</c> magic,
///         which is a strong enough anchor to search on instead: the sMesh table is
///         the longest 128-byte-strided run of records whose vertex pointer resolves
///         to such a descriptor. That recovers the table exactly with no CScene
///         assumptions at all.
///     </para>
///     <para>
///         sMesh: bounding-sphere centre +0x00, <b>radius +0x0C</b>, material checksum
///         +0x14 (the one field stable across PC, GameCube and both next-gen
///         revisions), <c>u16 indexCount</c> +0x24, <c>u16 vertexCount</c> +0x26,
///         attribute block +0x40 with its byte size at +0x4C, <b>index block +0x5C</b>,
///         vertex descriptor +0x60. Every pointer is scene-relative.
///     </para>
///     <para>
///         Positions live in a CHAIN OF BATCHES, which is why reading
///         <c>vertexCount</c> vertices contiguously from the first one is wrong for
///         every skinned mesh: a 48-byte descriptor holds the first batch's count at
///         +0x20 with data at +0x30, and each subsequent batch is preceded by a
///         16-byte header whose leading big-endian word is that batch's count. The
///         chain sums exactly to <c>vertexCount</c> (78+78+550+550 = 1256).
///     </para>
///     <para>
///         <b>Both revisions state the index block at +0x5C</b>, with the indices
///         beginning 0x20 bytes into it. An earlier pass located Project 8's indices
///         by searching for a <c>FACEF001 FACEF000</c> pair instead, which happened to
///         work there and found nothing at all in Proving Ground. Those words are
///         not a header: they are unresolved-pointer filler of the same family as
///         <c>BAADF00D</c>, and the two builds simply leave different slots
///         unresolved — Project 8 fills them into the index block's header while
///         Proving Ground fills them into the vertex descriptor's tail. Reading the
///         pointer the record actually states covers both, needs no search, and is
///         what lets Proving Ground parse.
///     </para>
///     <para>
///         Validation is the file's own bounding sphere, which a wrong base, stride,
///         batch walk or format cannot satisfy: across both Xbox 360 builds every
///         mesh reproduces its declared radius, and the highest index a mesh uses is
///         its own last vertex.
///     </para>
/// </summary>
internal static class NextGenLaterRevision
{
    private const int SMeshRecordSize = 128;
    private const int VertexStride = 32;
    private const int StripRestart = 0x7FFF;

    /// <summary>
    ///     48-byte buffer descriptor: the first batch's vertex count at +0x20, that
    ///     batch's bone palette at +0x24 (read but not consumed), two
    ///     unresolved-pointer filler slots, and the vertex data at +0x30. The
    ///     16-byte header introducing each later batch has the same shape.
    /// </summary>
    private const int BufferDescriptorSize = 0x30;

    /// <summary>Proving Ground's PS3 descriptor; see <see cref="DescriptorShape" />.</summary>
    private const int LongBufferDescriptorSize = 0x40;

    private const int BatchHeaderSize = 16;

    /// <summary>Both the attribute block and the index block start their payload here.</summary>
    private const int BlockHeaderSize = 0x20;

    /// <summary>
    ///     Attribute offsets are declared in a merged space whose first 32 bytes are
    ///     the position stream, so an offset of 32 means "the start of the attribute
    ///     entry".
    /// </summary>
    private const int PositionStreamSize = 32;

    /// <summary>Poison written into slots the build tool left unresolved.</summary>
    private const uint BadFood = 0xBAADF00D;

    private static ReadOnlySpan<byte> BufferMagic => [0xCA, 0xFE, 0xBA, 0xB4];

    public static XbxScene Parse(byte[] data, int scene, byte[]? vram = null)
    {
        var descriptors = FindDescriptors(data);
        if (!TryFindMeshTable(data, scene, descriptors, out var table, out var meshCount))
            throw new InvalidDataException("Next-gen later-revision scene has no resolvable sMesh table");

        RequireUsableTopology(data, scene, table, meshCount, vram);

        var materials = new Dictionary<uint, XbxMaterial>();
        var meshes = new List<XbxMesh>(meshCount);

        for (var i = 0; i < meshCount; i++)
        {
            var mesh = TryReadMesh(data, scene, descriptors, table + SMeshRecordSize * i, vram);
            if (mesh is null)
                continue;

            if (!materials.ContainsKey(mesh.MaterialChecksum))
                materials[mesh.MaterialChecksum] = BuildMaterial(mesh.MaterialChecksum);
            meshes.Add(mesh);
        }

        if (meshes.Count == 0)
            throw new InvalidDataException(
                $"Next-gen later-revision scene decoded none of its {meshCount} meshes: no " +
                "vertex stream and index block resolved. PlayStation 3 builds share this " +
                "container but move both into a sibling VRAM companion, which is not yet wired up");

        return new XbxScene
        {
            Materials = materials.Values.ToArray(),
            Sectors = [new XbxSector { Checksum = 0, BoneIndex = -1, Flags = 0, Meshes = meshes.ToArray() }],
            Links = []
        };
    }

    /// <summary>
    ///     Refuses the one combination whose topology we cannot yet read: Proving
    ///     Ground's PlayStation 3 build.
    ///     <para>
    ///         Its POSITIONS are fine — the long descriptor reproduces every declared
    ///         bounding sphere — but the sphere is order-insensitive and the indices
    ///         are wrong, which renders as a shattered fan of triangles. Measured with
    ///         a locality oracle (median triangle edge over the bounding radius, which
    ///         is small for real topology and large for garbage): Project 8's PS3
    ///         build scores 0.21-0.39 like its Xbox 360 sibling, while Proving
    ///         Ground's sits at 0.50-0.62 for single- and multi-batch meshes alike,
    ///         at every index-base offset from 0x00 to 0x60, and for the alternative
    ///         pointer and position sources tested. So it is neither a shift, nor the
    ///         batch chain, nor a swapped pointer.
    ///     </para>
    ///     <para>
    ///         The long descriptor is exactly this build (231 of 231 sampled PG-PS3
    ///         descriptors carry its marker, 0 of 708 elsewhere), so declining on it
    ///         costs nothing else. Emitting the geometry would pass the glTF
    ///         validator and the bounding-sphere gate while being visibly wrong.
    ///     </para>
    /// </summary>
    private static void RequireUsableTopology(
        byte[] data, int scene, int table, int meshCount, byte[]? vram)
    {
        if (vram is null)
            return;

        for (var i = 0; i < meshCount; i++)
        {
            var pointer = ReadUInt32(data, table + SMeshRecordSize * i + 0x60);
            if (pointer == uint.MaxValue)
                continue;

            var descriptor = scene + (long)pointer;
            if (descriptor + LongBufferDescriptorSize > data.Length)
                continue;

            if (DescriptorShape(data, (int)descriptor).DataOffset != LongBufferDescriptorSize)
                continue;

            throw new InvalidDataException(
                "Proving Ground's PlayStation 3 scenes decode their positions but not " +
                "their topology: the index buffer is not at the offset its record names " +
                "in the VRAM companion, so the file is declined rather than exported " +
                "with scrambled triangles. The Xbox 360 build of the same game is fully " +
                "supported, as is Project 8 on PlayStation 3");
        }
    }

    /// <summary>
    ///     Reads one sMesh. Returns null when the record does not yield both a
    ///     vertex stream and topology — the rest of the scene is independently
    ///     addressed, so one unreadable mesh must not fail the file.
    /// </summary>
    private static XbxMesh? TryReadMesh(
        byte[] data, int scene, HashSet<int> descriptors, int record, byte[]? vram)
    {
        if (record < 0 || record + SMeshRecordSize > data.Length)
            return null;

        var vertexCount = ReadUInt16(data, record + 0x26);
        var vertices = ReadVertices(data, scene, descriptors, record, vertexCount);
        if (vertices.Length == 0)
            return null;

        var indices = ReadIndices(data, scene, record, ReadUInt16(data, record + 0x24), vertices.Length, vram);

        // Vertices with no topology draw nothing, so a mesh whose index block does
        // not resolve is dropped rather than exported as a bare point cloud.
        if (indices.Length == 0)
            return null;

        var centre = ReadVec3(data, record);
        var radius = ReadSingle(data, record + 0x0C);
        if (!IsBoundingSphereCredible(vertices, centre, radius))
            return null;

        ApplyVertexAttributes(data, scene, record, vertices, vram);
        ComputeNormals(vertices, indices);

        return new XbxMesh
        {
            BsphereCenter = centre,
            BsphereRadius = radius,
            MaterialChecksum = ReadUInt32(data, record + 0x14),
            Vertices = vertices,
            FaceIndices = indices,
            IsPreTriangulated = true
        };
    }

    /// <summary>
    ///     The mesh's own bounding sphere, kept as a HARD gate rather than a
    ///     diagnostic. It is the only check specific to the vertex base being right:
    ///     the batch walk merely takes <c>vertexCount</c> vertices from wherever it
    ///     is pointed and "consumes exactly" whenever the first count is large
    ///     enough, so it cannot detect a wrong base at all.
    ///     <para>
    ///         The two populations separate by an enormous margin, which is what
    ///         makes a single loose threshold safe. Authored staleness is real but
    ///         tiny — 8 of 6,609 Xbox 360 meshes declare a short radius, topping out
    ///         at ratio 2.39, and the PS3 master of the same asset ships a corrected
    ///         radius over byte-identical positions, so it is data, not decode — while
    ///         a genuinely misread base lands at 1e36 or infinity. Anything finite and
    ///         under 100 is therefore kept.
    ///     </para>
    /// </summary>
    private static bool IsBoundingSphereCredible(XbxVertex[] vertices, Vector3 centre, float radius)
    {
        const float absurdRatio = 100f;

        if (!float.IsFinite(radius) || radius <= 0f)
            return true;

        var furthest = 0f;
        foreach (var vertex in vertices)
        {
            if (!float.IsFinite(vertex.Position.X) ||
                !float.IsFinite(vertex.Position.Y) ||
                !float.IsFinite(vertex.Position.Z))
                return false;

            furthest = MathF.Max(furthest, (vertex.Position - centre).Length());
        }

        return furthest / radius < absurdRatio;
    }

    /// <summary>
    ///     Two vertex layouts, chosen by the record itself. A real pointer at +0x60
    ///     means a <c>CAFEBAB4</c> descriptor and a batch chain of stride-32
    ///     position vertices. <c>0xFFFFFFFF</c> means the descriptor-less layout: the
    ///     whole vertex — position first — sits in the +0x40 block at a per-mesh
    ///     stride the record states as <c>+0x4C / vertexCount</c>, observed from 16 to
    ///     56 bytes. The two are perfectly disjoint and no file mixes them; every
    ///     <c>.mdl</c> and <c>.scn</c> uses the second, which is why the level scenes
    ///     carry no descriptor at all.
    /// </summary>
    private static XbxVertex[] ReadVertices(
        byte[] data, int scene, HashSet<int> descriptors, int record, int vertexCount)
    {
        var pointer = ReadUInt32(data, record + 0x60);
        if (pointer != uint.MaxValue)
        {
            var descriptor = scene + (long)pointer;
            return descriptor > int.MaxValue || !descriptors.Contains((int)descriptor)
                ? []
                : ReadBatchedVertices(data, (int)descriptor, vertexCount);
        }

        return ReadInlineVertices(data, scene, record, vertexCount);
    }

    private static XbxVertex[] ReadInlineVertices(byte[] data, int scene, int record, int vertexCount)
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
    ///     The scene states its own mesh table — offset at <c>scene+0x80</c>, count at
    ///     <c>scene+0x4C</c> — and using it removes 78 records the search-based
    ///     anchor invented while recovering 93 real meshes it missed.
    ///     <para>
    ///         <b>The offset word is treated as unverified.</b> It is constant across
    ///         every descriptor-bearing file in a build (352 in Project 8, 368 in
    ///         Proving Ground, 3,824 files with no exception), so the corpus cannot
    ///         distinguish "read this field" from "add this constant" — it only varies
    ///         across the <c>.mdl</c>/<c>.scn</c> families. It is therefore validated
    ///         hard and falls back to the search rather than being trusted outright.
    ///     </para>
    /// </summary>
    private static bool TryFindMeshTable(
        byte[] data, int scene, HashSet<int> descriptors, out int table, out int count)
    {
        if (TryReadStatedMeshTable(data, scene, out table, out count))
            return true;

        return TryScanForMeshTable(data, scene, descriptors, out table, out count);
    }

    private static bool TryReadStatedMeshTable(byte[] data, int scene, out int table, out int count)
    {
        table = 0;
        count = 0;
        if (scene < 0 || scene + 0x84 > data.Length)
            return false;

        var declaredCount = ReadUInt32(data, scene + 0x4C);
        var declaredOffset = ReadUInt32(data, scene + 0x80);
        if (declaredCount == 0 || declaredCount > 0xFFFF)
            return false;

        var candidate = scene + (long)declaredOffset;
        var span = (long)declaredCount * SMeshRecordSize;
        if (candidate < scene || candidate + span > data.Length)
            return false;

        // Every record must state a plausible vertex count and a resolvable stream,
        // otherwise this is not the table and the search decides instead.
        for (var i = 0; i < declaredCount; i++)
        {
            var record = (int)candidate + SMeshRecordSize * i;
            if (ReadUInt16(data, record + 0x26) == 0)
                return false;

            var pointer = ReadUInt32(data, record + 0x60);
            if (pointer == 0)
                return false;
        }

        table = (int)candidate;
        count = (int)declaredCount;
        return true;
    }

    private static bool TryScanForMeshTable(
        byte[] data, int scene, HashSet<int> descriptors, out int table, out int count)
    {
        table = 0;
        count = 0;

        var limit = data.Length - SMeshRecordSize;
        var offset = scene;
        while (offset <= limit)
        {
            if (!IsMeshRecord(data, scene, descriptors, offset))
            {
                offset += 4;
                continue;
            }

            var run = 1;
            while (IsMeshRecord(data, scene, descriptors, offset + SMeshRecordSize * run))
                run++;

            if (run > count)
            {
                table = offset;
                count = run;
            }

            offset += SMeshRecordSize * run;
        }

        return count > 0;
    }

    private static bool IsMeshRecord(byte[] data, int scene, HashSet<int> descriptors, int record)
    {
        if (record < 0 || record + SMeshRecordSize > data.Length)
            return false;

        var vertexCount = ReadUInt16(data, record + 0x26);
        if (vertexCount == 0)
            return false;

        var pointer = ReadUInt32(data, record + 0x60);
        if (pointer == 0)
            return false;

        // Descriptor-less records have no magic to anchor on, so they are held to
        // the fields they do state: a finite positive radius and a +0x40 block whose
        // declared byte size divides evenly into the vertex count.
        if (pointer == uint.MaxValue)
            return IsInlineMeshRecord(data, scene, record, vertexCount);

        var descriptor = scene + (long)pointer;
        if (descriptor > int.MaxValue || !descriptors.Contains((int)descriptor))
            return false;

        // The descriptor states its FIRST batch's count, which equals the mesh's
        // total only when the mesh is unbatched — so the test is an inequality.
        // Nothing in the descriptor announces batching: an earlier reading took
        // the word at +0x24 for a class flag, but it takes about seventy values
        // across the corpus and does not correlate with batching at all (its
        // multi-byte forms are ascending zero-terminated byte triples such as
        // 15 2B 2D 00, i.e. a per-batch bone palette, which we do not consume).
        // Requiring +0x24 to be a class rejected the table outright on most of
        // Proving Ground.
        var (countOffset, _) = DescriptorShape(data, (int)descriptor);
        var declared = ReadUInt32(data, (int)descriptor + countOffset);
        return declared > 0 && declared <= vertexCount;
    }

    private static bool IsInlineMeshRecord(byte[] data, int scene, int record, int vertexCount)
    {
        var radius = ReadSingle(data, record + 0x0C);
        if (!float.IsFinite(radius) || radius <= 0f)
            return false;

        var block = ReadUInt32(data, record + 0x40);
        var byteSize = ReadUInt32(data, record + 0x4C);
        if (block == uint.MaxValue || byteSize == 0 || byteSize == BadFood)
            return false;

        if (byteSize % (uint)vertexCount != 0 || byteSize / (uint)vertexCount < 12)
            return false;

        var start = scene + (long)block + BlockHeaderSize;
        return start >= 0 && byteSize <= data.Length - start;
    }

    /// <summary>
    ///     Walks the batch chain. Each batch after the first is introduced by a
    ///     16-byte header whose leading big-endian word is its vertex count.
    /// </summary>
    private static XbxVertex[] ReadBatchedVertices(byte[] data, int descriptor, int vertexCount)
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
                var normal = UnpackUnitVector(ReadUInt32(data, vertex + 0x10));
                vertices.Add(new XbxVertex
                {
                    Position = ReadVec3(data, vertex),
                    Normal = normal,
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

    /// <summary>
    ///     Where a descriptor states its first batch's count and where the vertex
    ///     data begins. Proving Ground's PlayStation 3 build uses a LONGER, 0x40-byte
    ///     descriptor — count at +0x30, data at +0x40 — and announces it by carrying
    ///     the <c>FACEF000 FACEF001</c> filler pair at +0x38/+0x3C. That marker is a
    ///     clean discriminator: 231 of 231 sampled PG-PS3 descriptors carry it and 0
    ///     of 708 across the other three builds do. The two readings are not
    ///     interchangeable — under the standard shape PG-PS3 reproduces 0 of 99
    ///     bounding spheres, and under the long shape it reproduces 99 of 99.
    /// </summary>
    private static (int CountOffset, int DataOffset) DescriptorShape(byte[] data, int descriptor)
    {
        if (descriptor + LongBufferDescriptorSize <= data.Length &&
            ReadUInt32(data, descriptor + 0x38) == 0xFACEF000 &&
            ReadUInt32(data, descriptor + 0x3C) == 0xFACEF001)
        {
            return (0x30, LongBufferDescriptorSize);
        }

        return (0x20, BufferDescriptorSize);
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
    ///         the colour's offset, so the first UV pair sits at
    ///         <c>(+0x1A) - 32</c> into each entry rather than at a fixed +4.
    ///     </para>
    ///     <para>
    ///         This runs only for the descriptor path. A descriptor-less mesh has
    ///         already consumed the +0x40 block as its interleaved vertex stream, and
    ///         its UV placement within that stride is not established.
    ///     </para>
    /// </summary>
    private static void ApplyVertexAttributes(
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
    ///         vertices — which share a position and are distinguished in-game only
    ///         by bone weights we do not export — would merge and take their
    ///         triangles with them as degenerates.
    ///     </para>
    /// </summary>
    private static void ComputeNormals(XbxVertex[] vertices, ushort[] indices)
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

    private static float HalfToSingle(ushort value)
    {
        return (float)BitConverter.UInt16BitsToHalf(value);
    }

    /// <summary>
    ///     An 11/11/10 packed signed unit vector: x in bits 0-10 over 1023, y in bits
    ///     11-21 over 1023, z in bits 22-31 over 511.
    ///     <para>
    ///         The vertex carries three of these, at +0x10, +0x14 and +0x18, and all
    ///         three come out unit length on 100.000% of 97,296 sampled vertices
    ///         against a 6.3% control (the position word read the same way). <b>+0x10
    ///         is the normal</b>: its mean signed dot with the facet normal of the
    ///         triangles we emit is +0.909 in Project 8 and +0.923 in Proving Ground,
    ///         while the other two sit at ±0.007, i.e. orthogonal — consistent with a
    ///         tangent frame, though which of the two is the tangent is not something
    ///         this reader needs or claims. That the dot is POSITIVE is a second
    ///         result worth having: it says the strip triangulation's winding agrees
    ///         with the authored normals.
    ///     </para>
    /// </summary>
    private static Vector3 UnpackUnitVector(uint packed)
    {
        return new Vector3(
            SignExtend(packed & 0x7FF, 11) / 1023f,
            SignExtend((packed >> 11) & 0x7FF, 11) / 1023f,
            SignExtend((packed >> 22) & 0x3FF, 10) / 511f);

        static int SignExtend(uint value, int bits)
        {
            var sign = 1u << (bits - 1);
            return (value & sign) != 0 ? (int)value - (1 << bits) : (int)value;
        }
    }

    /// <summary>
    ///     Indices are <c>indexCount</c> big-endian u16 tri-strip entries beginning
    ///     0x20 bytes into the block the record names at +0x5C, using 0x7FFF as the
    ///     strip restart. Any value that is neither a restart nor a vertex of this
    ///     mesh means the block was misidentified, so the mesh yields no topology
    ///     rather than nonsense triangles.
    /// </summary>
    private static ushort[] ReadIndices(
        byte[] data, int scene, int record, int indexCount, int vertexCount, byte[]? vram)
    {
        var pointer = ReadUInt32(data, record + 0x5C);
        if (indexCount <= 0 || vertexCount <= 0 || pointer == uint.MaxValue)
            return [];

        var (buffer, start) = ResolveBlock(data, scene, pointer, vram);
        var bytes = (long)indexCount * 2;
        if (start < 0 || bytes > buffer.Length - start)
            return [];

        var strip = new ushort[indexCount];
        for (var i = 0; i < indexCount; i++)
        {
            var value = ReadUInt16(buffer, (int)start + 2 * i);
            if (value != StripRestart && value >= vertexCount)
                return [];

            strip[i] = value;
        }

        return Triangulate(strip);
    }

    /// <summary>
    ///     Where a block pointer actually lands.
    ///     <para>
    ///         Xbox 360 keeps its blocks in the scene file, scene-relative, with a
    ///         0x20-byte header before the payload. <b>PlayStation 3 moves the
    ///         attribute stream and the index buffer into a sibling VRAM companion,
    ///         and addresses it with the SAME pointers as RAW offsets from byte 0 —
    ///         no scene base and no header skip.</b> Measured with controls: indices
    ///         land exactly at the raw offset on 104/104 sampled Project 8 PS3 meshes,
    ///         while the same pointers read against a DIFFERENT file's companion score
    ///         0/103.
    ///     </para>
    /// </summary>
    private static (byte[] Buffer, long Start) ResolveBlock(
        byte[] data, int scene, uint pointer, byte[]? vram)
    {
        return vram is null
            ? (data, scene + (long)pointer + BlockHeaderSize)
            : (vram, pointer);
    }

    private static ushort[] Triangulate(ushort[] strip)
    {
        var triangles = new List<ushort>();
        var start = 0;

        for (var i = 0; i <= strip.Length; i++)
        {
            if (i != strip.Length && strip[i] != StripRestart)
                continue;

            EmitStrip(strip, start, i, triangles);
            start = i + 1;
        }

        return triangles.ToArray();
    }

    /// <summary>Emits one restart-delimited run, alternating winding as strips do.</summary>
    private static void EmitStrip(ushort[] strip, int start, int end, List<ushort> triangles)
    {
        for (var f = start + 2; f < end; f++)
        {
            var even = (f - start) % 2 == 0;
            var i0 = strip[f - 2];
            var i1 = even ? strip[f - 1] : strip[f];
            var i2 = even ? strip[f] : strip[f - 1];
            if (i0 == i1 || i1 == i2 || i0 == i2)
                continue;

            triangles.Add(i0);
            triangles.Add(i1);
            triangles.Add(i2);
        }
    }

    private static HashSet<int> FindDescriptors(byte[] data)
    {
        var found = new HashSet<int>();
        var span = data.AsSpan();

        for (var o = 0; o + BufferDescriptorSize <= data.Length; o += 4)
        {
            if (!span.Slice(o, 4).SequenceEqual(BufferMagic))
                continue;

            // Four consecutive copies is the descriptor's own 16-byte sentinel.
            if (o + 16 <= data.Length &&
                span.Slice(o + 4, 4).SequenceEqual(BufferMagic) &&
                span.Slice(o + 8, 4).SequenceEqual(BufferMagic) &&
                span.Slice(o + 12, 4).SequenceEqual(BufferMagic))
                found.Add(o);
        }

        return found;
    }

    private static XbxMaterial BuildMaterial(uint checksum)
    {
        return new XbxMaterial
        {
            Checksum = checksum,
            NameChecksum = checksum,
            NumPasses = 1,
            SingleSided = false,
            Passes = [new XbxPass { TextureChecksum = 0, HasColor = false }]
        };
    }

    private static uint ReadUInt32(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o));
    }

    private static ushort ReadUInt16(byte[] d, int o)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o));
    }

    private static float ReadSingle(byte[] d, int o)
    {
        return BinaryPrimitives.ReadSingleBigEndian(d.AsSpan(o));
    }

    private static Vector3 ReadVec3(byte[] d, int o)
    {
        return new Vector3(ReadSingle(d, o), ReadSingle(d, o + 4), ReadSingle(d, o + 8));
    }
}
