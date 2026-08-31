using static NeversoftMultitool.Core.Formats.Mesh.XbxScene.NextGenSceneBinary;

namespace NeversoftMultitool.Core.Formats.Mesh.XbxScene;

/// <summary>
///     Finds the sMesh table in a later-revision next-gen scene (Project 8, Proving
///     Ground).
///     <para>
///         The scene STATES its own table — offset at <c>scene+0x80</c>, count at
///         <c>scene+0x4C</c> — and using it removes 78 records the search-based anchor
///         invented while recovering 93 real meshes it missed. Sixty of those 78
///         passed every oracle VACUOUSLY, with <c>indexCount</c> and radius both zero:
///         a zero-length index loop cannot produce an out-of-range index, and the
///         sphere check is guarded on a positive radius.
///     </para>
///     <para>
///         <b>The offset word is nonetheless treated as unverified.</b> It is constant
///         across every descriptor-bearing file in a build (352 in Project 8, 368 in
///         Proving Ground, 3,824 files with no exception), so the corpus cannot
///         distinguish "read this field" from "add this constant"; it only varies
///         across the <c>.mdl</c>/<c>.scn</c> families. It is therefore validated hard
///         and falls back to the search rather than being trusted outright.
///     </para>
/// </summary>
internal static class NextGenMeshTable
{
    internal static bool TryFind(
        byte[] data, int scene, HashSet<int> descriptors, out int table, out int count)
    {
        if (TryReadStated(data, scene, out table, out count))
            return true;

        return TryScan(data, scene, descriptors, out table, out count);
    }

    private static bool TryReadStated(byte[] data, int scene, out int table, out int count)
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

            if (ReadUInt32(data, record + 0x60) == 0)
                return false;
        }

        table = (int)candidate;
        count = (int)declaredCount;
        return true;
    }

    /// <summary>
    ///     The longest 128-strided run of records that all resolve. A 16-byte magic
    ///     plus an agreeing vertex count is far too specific to line up by chance, so
    ///     the longest run IS the table.
    /// </summary>
    private static bool TryScan(
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
        // Nothing in the descriptor announces batching: an earlier reading took the
        // word at +0x24 for a class flag, but it takes about seventy values across
        // the corpus and does not correlate with batching at all (its multi-byte
        // forms are ascending zero-terminated byte triples such as 15 2B 2D 00, i.e.
        // a per-batch bone palette, which we do not consume). Requiring +0x24 to be
        // a class rejected the table outright on most of Proving Ground.
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
}
