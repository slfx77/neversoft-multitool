using System.Numerics;
using static NeversoftMultitool.Core.Formats.Mesh.XbxScene.NextGenSceneBinary;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Project 8 and Proving Ground scene geometry (derived 2026-08-28). Same
///     <c>FAAABACA</c> container and same 128-byte sMesh record as
///     <see cref="NextGenSceneFile" />, but a later revision that moves nearly
///     everything else. The two games are ONE format: a cutscene shipping in both
///     builds has byte-identical mesh records apart from four unresolved pointer
///     slots, and identical vertex bytes apart from one word.
///     <para>
///         sMesh: bounding-sphere centre +0x00, <b>radius +0x0C</b>, material checksum
///         +0x14 (the one field stable across PC, GameCube and both next-gen
///         revisions), <c>u16 indexCount</c> +0x24, <c>u16 vertexCount</c> +0x26,
///         attribute block +0x40 with its byte size at +0x4C, <b>index block +0x5C</b>,
///         vertex descriptor +0x60. Every pointer is scene-relative on Xbox 360, and a
///         raw offset into a VRAM companion on PlayStation 3.
///     </para>
///     <para>
///         <b>Both revisions state the index block at +0x5C</b>, with the indices
///         beginning 0x20 bytes into it. An earlier pass located Project 8's indices
///         by searching for a <c>FACEF001 FACEF000</c> pair instead, which happened to
///         work there and found nothing at all in Proving Ground. Those words are not
///         a header: they are unresolved-pointer filler of the same family as
///         <c>BAADF00D</c>, and the two builds simply leave different slots
///         unresolved — Project 8 fills them into the index block's header while
///         Proving Ground fills them into the vertex descriptor's tail. Reading the
///         pointer the record actually states covers both, needs no search, and is
///         what lets Proving Ground parse.
///     </para>
///     <para>
///         Table location lives in <see cref="NextGenMeshTable" />, vertex decoding in
///         <see cref="NextGenVertexDecoder" />, and the shared addressing rules in
///         <see cref="NextGenSceneBinary" />.
///     </para>
/// </summary>
internal static class NextGenLaterRevision
{
    public static XbxScene Parse(byte[] data, int scene, byte[]? vram = null)
    {
        var descriptors = FindDescriptors(data);
        if (!NextGenMeshTable.TryFind(data, scene, descriptors, out var table, out var meshCount))
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
                "vertex stream and index block resolved");

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
    ///         is small for real topology and large for garbage): Project 8's PS3 build
    ///         scores 0.21-0.39 like its Xbox 360 sibling, while Proving Ground's sits
    ///         at 0.50-0.62 for single- and multi-batch meshes alike, at every
    ///         index-base offset from 0x00 to 0x60, and for the alternative pointer and
    ///         position sources tested. So it is neither a shift, nor the batch chain,
    ///         nor a swapped pointer.
    ///     </para>
    ///     <para>
    ///         The long descriptor is exactly this build (231 of 231 sampled PG-PS3
    ///         descriptors carry its marker, 0 of 708 elsewhere), so declining on it
    ///         costs nothing else. Emitting the geometry would pass the glTF validator
    ///         and the bounding-sphere gate while being visibly wrong.
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
    ///     Reads one sMesh. Returns null when the record does not yield both a vertex
    ///     stream and topology — the rest of the scene is independently addressed, so
    ///     one unreadable mesh must not fail the file.
    /// </summary>
    private static XbxMesh? TryReadMesh(
        byte[] data, int scene, HashSet<int> descriptors, int record, byte[]? vram)
    {
        if (record < 0 || record + SMeshRecordSize > data.Length)
            return null;

        var vertexCount = ReadUInt16(data, record + 0x26);
        var vertices = NextGenVertexDecoder.Read(data, scene, descriptors, record, vertexCount);
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

        NextGenVertexDecoder.ApplyAttributes(data, scene, record, vertices, vram);
        NextGenVertexDecoder.ComputeNormals(vertices, indices);

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
    ///     the batch walk merely takes <c>vertexCount</c> vertices from wherever it is
    ///     pointed and "consumes exactly" whenever the first count is large enough, so
    ///     it cannot detect a wrong base at all.
    ///     <para>
    ///         The two populations separate by an enormous margin, which is what makes
    ///         a single loose threshold safe. Authored staleness is real but tiny — 8
    ///         of 6,609 Xbox 360 meshes declare a short radius, topping out at ratio
    ///         2.39, and the PS3 master of the same asset ships a corrected radius over
    ///         byte-identical positions, so it is data, not decode — while a genuinely
    ///         misread base lands at 1e36 or infinity. Anything finite and under 100 is
    ///         therefore kept.
    ///     </para>
    /// </summary>
    private static bool IsBoundingSphereCredible(XbxVertex[] vertices, Vector3 centre, float radius)
    {
        const float absurdRatio = 100f;

        if (!float.IsFinite(radius) || radius <= 0f)
            return true;

        var furthest = 0f;
        foreach (var position in vertices.Select(static vertex => vertex.Position))
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z))
                return false;

            furthest = MathF.Max(furthest, (position - centre).Length());
        }

        return furthest / radius < absurdRatio;
    }

    /// <summary>
    ///     Indices are <c>indexCount</c> big-endian u16 tri-strip entries beginning
    ///     0x20 bytes into the block the record names at +0x5C (or at the raw offset,
    ///     in a PS3 VRAM companion), using 0x7FFF as the strip restart. Any value that
    ///     is neither a restart nor a vertex of this mesh means the block was
    ///     misidentified, so the mesh yields no topology rather than nonsense
    ///     triangles.
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
}
