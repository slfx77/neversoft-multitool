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

    /// <summary>
    ///     Parse the THAW MDL preamble metadata. Returns null only for invalid inputs.
    ///     Valid world-zone MDLs return a preamble with no sentinel, no trailer, and no bones.
    /// </summary>
    public static Preamble? TryParse(byte[] data, int vifStart)
    {
        if (data.Length == 0 || vifStart < 0 || vifStart > data.Length)
            return null;

        var (sentinelStart, sentinelEnd) = FindSentinel(data, Math.Min(vifStart, MaxSentinelScan));
        if (sentinelStart < 0 || sentinelEnd < 0)
        {
            return new Preamble
            {
                VifStart = vifStart,
                Bones = Array.Empty<MdlBone>()
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
            Trailer = trailer
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
