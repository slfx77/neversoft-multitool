using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Parser for the preamble section of THAW PS2 PAK-extracted .mdl files.
///     The preamble may contain a trailing object trailer block before the CDCDCDCD sentinel
///     and a post-sentinel 80-byte bone record array. World-zone MDLs do not contain the
///     sentinel-backed object metadata block.
/// </summary>
public static class Ps2MdlPreamble
{
    public sealed record Preamble
    {
        public required int VifStart { get; init; }
        public int? SentinelStart { get; init; }
        public int? SentinelEnd { get; init; }
        public uint? BoneSectionSize { get; init; }
        public uint? BoneSectionPadding { get; init; }
        public IReadOnlyList<MdlBone> Bones { get; init; } = Array.Empty<MdlBone>();
        public ObjectTrailer? Trailer { get; init; }

        /// <summary>
        ///     0x50-byte preamble records that carry per-class rotation + local size for worldzone
        ///     object placement. Keyed by byte offset within the MDL so worldzone placement items
        ///     (Ps2ObjectPlacementFile.PlacementItem.Field_44) can look up records directly.
        /// </summary>
        public IReadOnlyDictionary<int, PreambleRecord> Records { get; init; }
            = new Dictionary<int, PreambleRecord>();
    }

    /// <summary>
    ///     Per-class record stored in the MDL preamble at a 0x50-byte stride. Signature
    ///     0x4B189680 sits at record+0x18. The 4-floats at +0x20..+0x2C form a unit quaternion
    ///     multiplied by a scalar magnitude; normalizing yields the object's rotation.
    ///     Full format reference: tools/ghidra/thaw-ps2/output/phase400_91e1028d_full_layout.md
    /// </summary>
    public sealed record PreambleRecord
    {
        public required int Offset { get; init; }
        public required uint ClassHash { get; init; }
        public required byte Sequence { get; init; }
        public required Quaternion Rotation { get; init; }
        public required Vector3 Size { get; init; }
        public required uint Flags { get; init; }

        /// <summary>
        ///     Raw (x, y, z) floats at record +0x20. For object MDLs this is a rotation quaternion
        ///     (see <see cref="Rotation" />); for level MDLs (worldzone shell/CAP geometry chunks)
        ///     it's a world-space bounding sphere centre used as per-batch placement.
        /// </summary>
        public required Vector3 Centre { get; init; }

        /// <summary>
        ///     u32 at record +0x40. For level MDL leaves (<see cref="IsLeaf" />) this is a file
        ///     offset into the VIF region pointing at the leaf's vertex data chunk. For internal
        ///     tree nodes (non-leaf) it is a child record offset. The THAW engine rebases this
        ///     field from file-offset to EE-absolute pointer on load, but pre-relocation (our
        ///     case) it is a plain byte offset into the MDL file.
        /// </summary>
        public required uint Field40 { get; init; }

        /// <summary>
        ///     u32 at record +0x48. Empirically always either 0xFFFFFFFF (no sibling) or a file
        ///     offset pointing into the preamble-record table, so interpreted as a sibling
        ///     reference in the CGeomNode-style tree.
        /// </summary>
        public required uint Field48 { get; init; }

        /// <summary>
        ///     Leaf test derived from <see cref="Flags" /> bit 1 (0x02). Verified against the
        ///     BH sample: ~3,977/5,649 records are leaves, all with <see cref="Field40" />
        ///     pointing into the VIF region.
        /// </summary>
        public bool IsLeaf => (Flags & 0x2u) != 0;
    }

    public sealed record ObjectTrailer
    {
        public required int HeaderOffset { get; init; }
        public required uint PrefixZero { get; init; }
        public required uint Marker { get; init; }
        public required uint Count { get; init; }
        public required uint RawPointer { get; init; }
        public IReadOnlyList<uint> Indices { get; init; } = Array.Empty<uint>();
    }

    /// <summary>
    ///     Bone record from the post-sentinel section.
    ///     Each record is 80 bytes: 16B header + 64B row-major 4x4 float matrix.
    ///     Row 3 of the matrix contains the position (X, Y, Z, 1.0).
    /// </summary>
    public readonly record struct MdlBone(uint Checksum, uint ParentChecksum, ushort Index, Matrix4x4 Transform)
    {
        public Vector3 Position => new(Transform.M41, Transform.M42, Transform.M43);
    }

    private const uint CdcdSentinel = 0xCDCDCDCD;
    private const uint ObjectTrailerMarker = 0x00010100;
    private const int BoneRecordSize = 80;
    private const int BoneHeaderSize = 16;
    private const int BoneMatrixOffset = 16;
    private const int MaxSentinelScan = 0x10000;

    private const uint PreambleRecordSig = 0x4B189680;
    private const int PreambleRecordSigOffset = 0x18;
    private const int PreambleRecordStride = 0x50;

    /// <summary>
    ///     Parse the THAW MDL preamble metadata. Returns null only for invalid inputs.
    ///     Valid world-zone MDLs return a preamble with no sentinel, no trailer, and no bones.
    /// </summary>
    public static Preamble? TryParse(byte[] data, int vifStart)
    {
        if (data.Length == 0 || vifStart < 0 || vifStart > data.Length)
            return null;

        var records = ParsePreambleRecords(data);
        var (sentinelStart, sentinelEnd) = FindSentinel(data, Math.Min(vifStart, MaxSentinelScan));
        if (sentinelStart < 0 || sentinelEnd < 0)
        {
            return new Preamble
            {
                VifStart = vifStart,
                Bones = Array.Empty<MdlBone>(),
                Records = records
            };
        }

        var trailer = TryParseObjectTrailer(data, sentinelStart);
        var bones = Array.Empty<MdlBone>();
        uint? boneSectionSize = null;
        uint? boneSectionPadding = null;

        if (TryParseBoneSection(data, sentinelEnd, out var parsedSize, out var parsedPadding, out var parsedBones))
        {
            boneSectionSize = parsedSize;
            boneSectionPadding = parsedPadding;
            bones = parsedBones;
        }

        return new Preamble
        {
            VifStart = vifStart,
            SentinelStart = sentinelStart,
            SentinelEnd = sentinelEnd,
            BoneSectionSize = boneSectionSize,
            BoneSectionPadding = boneSectionPadding,
            Bones = bones,
            Trailer = trailer,
            Records = records
        };
    }

    /// <summary>
    ///     Scan the MDL for 0x50-byte preamble records. Each record starts at an offset where
    ///     the 0x4B189680 signature appears at +0x18 and another signature follows at +0x50
    ///     relative to the previous record start.
    /// </summary>
    private static Dictionary<int, PreambleRecord> ParsePreambleRecords(byte[] data)
    {
        var records = new Dictionary<int, PreambleRecord>();

        // Collect all signature hits at 4-byte alignment, then group adjacent ones that are
        // exactly PreambleRecordStride apart.
        var firstSig = -1;
        for (var i = 0; i + 4 <= data.Length; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != PreambleRecordSig)
                continue;
            firstSig = i;
            break;
        }

        if (firstSig < PreambleRecordSigOffset)
            return records;

        var recStart = firstSig - PreambleRecordSigOffset;
        while (recStart >= 0 && recStart + PreambleRecordStride <= data.Length)
        {
            var sigAt = recStart + PreambleRecordSigOffset;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sigAt)) != PreambleRecordSig)
                break;

            records[recStart] = ReadPreambleRecord(data, recStart);
            recStart += PreambleRecordStride;
        }

        return records;
    }

    private static PreambleRecord ReadPreambleRecord(byte[] data, int offset)
    {
        var span = data.AsSpan(offset);

        var classHash = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var seqWord = BinaryPrimitives.ReadUInt32LittleEndian(span[0x10..]);
        var sequence = (byte)((seqWord >> 8) & 0xFF);

        // 4 floats at +0x20..+0x2C = unit quaternion * scalar magnitude (in qx, qy, qz, qw order).
        var qx = BinaryPrimitives.ReadSingleLittleEndian(span[0x20..]);
        var qy = BinaryPrimitives.ReadSingleLittleEndian(span[0x24..]);
        var qz = BinaryPrimitives.ReadSingleLittleEndian(span[0x28..]);
        var qw = BinaryPrimitives.ReadSingleLittleEndian(span[0x2C..]);
        var mag = MathF.Sqrt(qx * qx + qy * qy + qz * qz + qw * qw);
        var rotation = mag > 0f
            ? new Quaternion(qx / mag, qy / mag, qz / mag, qw / mag)
            : Quaternion.Identity;

        var sx = BinaryPrimitives.ReadSingleLittleEndian(span[0x30..]);
        var sy = BinaryPrimitives.ReadSingleLittleEndian(span[0x34..]);
        var sz = BinaryPrimitives.ReadSingleLittleEndian(span[0x38..]);

        var flags = BinaryPrimitives.ReadUInt32LittleEndian(span[0x3C..]);
        var field40 = BinaryPrimitives.ReadUInt32LittleEndian(span[0x40..]);
        var field48 = BinaryPrimitives.ReadUInt32LittleEndian(span[0x48..]);

        return new PreambleRecord
        {
            Offset = offset,
            ClassHash = classHash,
            Sequence = sequence,
            Rotation = rotation,
            Size = new Vector3(sx, sy, sz),
            Flags = flags,
            Centre = new Vector3(qx, qy, qz),
            Field40 = field40,
            Field48 = field48
        };
    }

    private static bool TryParseBoneSection(byte[] data, int sentinelEnd,
        out uint totalSize, out uint padding, out MdlBone[] bones)
    {
        totalSize = 0;
        padding = 0;
        bones = Array.Empty<MdlBone>();

        if (sentinelEnd + BoneHeaderSize > data.Length)
            return false;

        var span = data.AsSpan(sentinelEnd);
        totalSize = BinaryPrimitives.ReadUInt32LittleEndian(span);
        padding = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var boneCount = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
        var boneCount2 = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);

        if (boneCount != boneCount2)
            return false;
        if (boneCount == 0 || boneCount > 256)
            return false;
        if (totalSize != BoneHeaderSize + boneCount * BoneRecordSize)
            return false;

        var boneStart = sentinelEnd + BoneHeaderSize;
        var byteCount = checked((int)(boneCount * BoneRecordSize));
        if (boneStart + byteCount > data.Length)
            return false;

        bones = new MdlBone[boneCount];
        for (var i = 0; i < boneCount; i++)
        {
            var off = boneStart + i * BoneRecordSize;
            bones[i] = ReadBone(data, off);
        }

        return true;
    }

    private static ObjectTrailer? TryParseObjectTrailer(byte[] data, int sentinelStart)
    {
        for (var markerOffset = sentinelStart - 12; markerOffset >= 4; markerOffset -= 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(markerOffset)) != ObjectTrailerMarker)
                continue;

            var headerOffset = markerOffset - 4;
            var prefixZero = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(headerOffset));
            var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(markerOffset + 4));
            var rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(markerOffset + 8));
            if (prefixZero != 0 || count == 0 || count > 2048)
                continue;

            var indicesStart = markerOffset + 12;
            var indicesByteCount = checked((int)count * sizeof(uint));
            if (indicesStart + indicesByteCount != sentinelStart || indicesStart + indicesByteCount > data.Length)
                continue;

            var indices = new uint[count];
            for (var i = 0; i < count; i++)
                indices[i] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(indicesStart + i * sizeof(uint)));

            return new ObjectTrailer
            {
                HeaderOffset = headerOffset,
                PrefixZero = prefixZero,
                Marker = ObjectTrailerMarker,
                Count = count,
                RawPointer = rawPointer,
                Indices = indices
            };
        }

        return null;
    }

    /// <summary>
    ///     Find the start and end of the CDCDCDCD sentinel sequence.
    ///     Returns (-1, -1) when the sentinel is absent.
    /// </summary>
    private static (int Start, int End) FindSentinel(byte[] data, int limit)
    {
        for (var i = 0; i < limit - 3 && i < data.Length - 3; i += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i)) != CdcdSentinel)
                continue;

            var end = i + sizeof(uint);
            while (end + sizeof(uint) <= data.Length && end < limit &&
                   BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(end)) == CdcdSentinel)
            {
                end += sizeof(uint);
            }

            return (i, end);
        }

        return (-1, -1);
    }

    private static MdlBone ReadBone(byte[] data, int offset)
    {
        var span = data.AsSpan(offset);
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(span);
        var parentChecksum = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
        var index = BinaryPrimitives.ReadUInt16LittleEndian(span[8..]);

        var m = offset + BoneMatrixOffset;
        var matrix = new Matrix4x4(
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 4)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 8)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 12)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 16)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 20)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 24)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 28)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 32)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 36)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 40)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 44)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 48)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 52)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 56)),
            BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(m + 60)));

        return new MdlBone(checksum, parentChecksum, index, matrix);
    }
}
