using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     The <c>bounds.bin</c> companion of a carved N64 model bundle: one
///     bounding record per MESH, big-endian throughout.
///     <para>
///         Layout — <c>u32 count</c>, then <c>count</c> x 24-byte records:
///         <c>u32 kind</c>, <c>u32 radius</c> (20.12 fixed point),
///         <c>6x i16</c> per-axis min/max in x, y, z order, then two i16 the
///         renderer uses for its own purposes. A file may carry four bytes of
///         zero padding after the last record (80 of 116 THPS2 bundles do), so
///         the parse accepts any tail rather than pinning the length.
///     </para>
///     <para>
///         <c>radius</c> is a stored bounding-SPHERE radius, not a quantity
///         derived from the box: it equals the farthest corner's distance from
///         the model origin within 2% for 11,383 of 15,473 THPS2 records but
///         not the rest, and <c>kind</c> (8 or 10) does not explain the
///         difference. It is therefore used only as a monotone SIZE feature —
///         see <see cref="N64BundleClassifier" /> — and never as exact geometry.
///     </para>
/// </summary>
public static class N64BoundsFile
{
    /// <summary>Bytes per bounding record.</summary>
    public const int RecordSize = 24;

    private const int HeaderSize = 4;
    private const float FixedPointScale = 4096f;

    /// <summary>One mesh's bounds. Extents are raw N64 units; radius is world units.</summary>
    public readonly record struct Record(
        uint Kind,
        float Radius,
        short MinX,
        short MaxX,
        short MinY,
        short MaxY,
        short MinZ,
        short MaxZ);

    /// <summary>
    ///     Reads every record, or an empty list when the companion is absent,
    ///     a 4-byte stub, or too short for the count it declares. A stub bundle
    ///     is normal — 144 of 594 carved bundles are authored-empty — so this
    ///     never throws.
    /// </summary>
    public static IReadOnlyList<Record> Parse(ReadOnlySpan<byte> data)
    {
        if (!TryReadCount(data, out var count) || !HasOrderedExtents(data, count))
            return [];

        var records = new Record[count];
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderSize + i * RecordSize;
            records[i] = new Record(
                BinaryPrimitives.ReadUInt32BigEndian(data[offset..]),
                BinaryPrimitives.ReadUInt32BigEndian(data[(offset + 4)..]) / FixedPointScale,
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 8)..]),
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 10)..]),
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 12)..]),
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 14)..]),
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 16)..]),
                BinaryPrimitives.ReadInt16BigEndian(data[(offset + 18)..]));
        }

        return records;
    }

    /// <summary>
    ///     Largest bounding radius in the bundle, or 0 when there is none. This
    ///     is the world-scale feature; reading it does not allocate the records.
    /// </summary>
    public static float MaxRadius(ReadOnlySpan<byte> data)
    {
        if (!TryReadCount(data, out var count) || !HasOrderedExtents(data, count))
            return 0f;

        var largest = 0u;
        for (var i = 0; i < count; i++)
        {
            var radius = BinaryPrimitives.ReadUInt32BigEndian(data[(HeaderSize + i * RecordSize + 4)..]);
            if (radius > largest)
                largest = radius;
        }

        return largest / FixedPointScale;
    }

    private static bool TryReadCount(ReadOnlySpan<byte> data, out int count)
    {
        count = 0;
        if (data.Length < HeaderSize + RecordSize)
            return false;

        var declared = BinaryPrimitives.ReadUInt32BigEndian(data);
        if (declared == 0 || declared > (data.Length - HeaderSize) / RecordSize)
            return false;

        count = (int)declared;
        return true;
    }

    private static bool HasOrderedExtents(ReadOnlySpan<byte> data, int count)
    {
        for (var i = 0; i < count; i++)
        {
            var offset = HeaderSize + i * RecordSize + 8;
            var minX = BinaryPrimitives.ReadInt16BigEndian(data[offset..]);
            var maxX = BinaryPrimitives.ReadInt16BigEndian(data[(offset + 2)..]);
            var minY = BinaryPrimitives.ReadInt16BigEndian(data[(offset + 4)..]);
            var maxY = BinaryPrimitives.ReadInt16BigEndian(data[(offset + 6)..]);
            var minZ = BinaryPrimitives.ReadInt16BigEndian(data[(offset + 8)..]);
            var maxZ = BinaryPrimitives.ReadInt16BigEndian(data[(offset + 10)..]);
            if (minX > maxX || minY > maxY || minZ > maxZ)
                return false;
        }

        return true;
    }
}
