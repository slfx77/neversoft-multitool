using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Tony Hawk's Downhill Jam's GBA rider geometry.  Downhill Jam was made by
///     Visual Impact and does not use the Vicarious Visions isometric level/model
///     layouts used by the earlier carts.  The ROM contains indexed triangle
///     banks, but its vertices are authored in 13 independent rigid-part spaces;
///     applying a 13-part pose is required before the banks form a rider.
///
///     <para>The layout was closed against a live gameplay object: EWRAM retained
///     ROM pointers to the 138-vertex bank at <c>0x08EB7A9C</c> and the 110-face
///     bank at <c>0x08EB7EEC</c>.  Walking the surrounding record revealed the
///     same self-validating structure for all 24 rider variants:</para>
///     <code>
/// +0x00 u16 marker (=128; its runtime meaning is not established),
///       u16 groupCount (=13)
/// +0x04 u16 vertexCount[groupCount]
/// +0x1E ... model metadata (not decoded yet)
/// +0x44 u16 faceCount[groupCount]
/// +0x5E ... model metadata
/// +0x84 Vertex[sum(vertexCount)]       // s16 x,y,z; u16 packedNormal
///       Face[sum(faceCount)]           // u8 v0,v1,v2,shadeCode
///       u32 0x01234567                 // exact terminator
///     </code>
///
///     <para>The group sums determine both raw banks without pointers.  A candidate is
///     accepted only when every triangle index is inside the derived vertex bank,
///     the highest referenced vertex closes that bank exactly, and the terminator
///     follows the final face.  This avoids fixed ROM addresses and rejects the
///     many unrelated uses of the same terminator in track data.</para>
/// </summary>
public static class GbaDhjModel
{
    public const int GroupCount = 13;
    public const int HeaderSize = 0x84;
    public const int VertexRecordSize = 8;
    public const int FaceRecordSize = 4;
    public const ushort HeaderMarker = 128;

    public const int PoseRecordSize = 0x50;
    public const int PoseDataOffset = 2;
    public const int PartPoseSize = 6;

    private const int FaceCountsOffset = 0x44;
    private const uint Terminator = 0x01234567;

    public sealed record ModelInfo(
        int Index,
        int HeaderOffset,
        ushort[] VertexCounts,
        ushort[] FaceCounts,
        int VertexDataOffset,
        int FaceDataOffset,
        int EndOffset)
    {
        public int VertexCount => VertexCounts.Sum(static value => value);
        public int FaceCount => FaceCounts.Sum(static value => value);
    }

    public readonly record struct Vertex(short X, short Y, short Z, ushort PackedNormal);

    /// <summary>
    ///     One rigid-part record from a <c>0x50</c>-byte animation frame.  The
    ///     first two translations are signed bytes, while Z and the three
    ///     rotations are unsigned bytes.  Rotations use a 256-step turn.
    /// </summary>
    public readonly record struct PartPose(
        sbyte TranslationX,
        sbyte TranslationY,
        byte TranslationZ,
        byte RotationX,
        byte RotationY,
        byte RotationZ);

    public sealed record PoseFrame(int Offset, ushort Header, PartPose[] Parts);

    /// <summary>
    ///     DHJ's animation directory.  Clip offsets are relative to four bytes
    ///     into the directory header.  Every bounded clip contains N pose frames
    ///     followed by one u32 playback/control value.
    /// </summary>
    public sealed record PoseLibraryInfo(
        int HeaderOffset,
        int[] ClipOffsets,
        int[] ClipFrameCounts)
    {
        public int ClipCount => ClipOffsets.Length;
    }

    /// <summary>
    ///     <paramref name="Group"/> is the authored face group.  The last source
    ///     byte is retained as <paramref name="ShadeCode"/>; its palette/ramp
    ///     binding is not yet decoded and exporters must not present it as RGB.
    /// </summary>
    public readonly record struct Face(int Group, byte V0, byte V1, byte V2, byte ShadeCode);

    /// <summary>Find all structurally closed rider meshes in a Downhill Jam ROM.</summary>
    public static IReadOnlyList<ModelInfo> FindModels(ReadOnlySpan<byte> rom)
    {
        if (!IsDownhillJam(rom))
            return [];

        var result = new List<ModelInfo>();
        for (var offset = 0; offset + HeaderSize <= rom.Length; offset += 4)
        {
            if (BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset, 2)) != HeaderMarker
                || BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 2, 2)) != GroupCount)
            {
                continue;
            }

            if (TryReadModel(rom, offset, result.Count) is { } model)
                result.Add(model);
        }

        return result;
    }

    /// <summary>
    ///     Locate DHJ's pose directory without relying on its retail ROM address.
    ///     The shipped US image has one match (94 clips at <c>0x00E71808</c>).
    /// </summary>
    public static IReadOnlyList<PoseLibraryInfo> FindPoseLibraries(ReadOnlySpan<byte> rom)
    {
        if (!IsDownhillJam(rom))
            return [];

        var result = new List<PoseLibraryInfo>();
        for (var header = 0; header <= rom.Length - 0x14; header += 4)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(header, 4)) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(header + 4, 4)) != PoseRecordSize
                || BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(header + 8, 4)) != 0x10
                || BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(header + 0xC, 2)) != GroupCount)
            {
                continue;
            }

            var clipCount = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(header + 0xE, 2));
            if (clipCount is < 2 or > 512 || header > rom.Length - 0x10 - clipCount * 4)
                continue;

            var relativeBase = header + 4;
            var expectedFirstRelative = 0x10 + clipCount * 4;
            var offsets = new int[clipCount];
            var valid = true;
            for (var i = 0; i < clipCount; i++)
            {
                var relative = BinaryPrimitives.ReadUInt32LittleEndian(
                    rom.Slice(header + 0x10 + i * 4, 4));
                var absolute = (long)relativeBase + relative;
                if (relative > int.MaxValue || absolute < 0 || absolute > rom.Length - PoseRecordSize
                    || i == 0 && relative != expectedFirstRelative
                    || i > 0 && absolute <= offsets[i - 1])
                {
                    valid = false;
                    break;
                }

                offsets[i] = (int)absolute;
            }

            if (!valid)
                continue;

            // Every clip except the final one has a following directory offset,
            // so its exact frame count closes as N*0x50 plus one u32 trailer.
            var frameCounts = new int[clipCount];
            for (var i = 0; i < clipCount - 1; i++)
            {
                var length = offsets[i + 1] - offsets[i];
                if (length < PoseRecordSize + 4 || (length - 4) % PoseRecordSize != 0)
                {
                    valid = false;
                    break;
                }

                frameCounts[i] = (length - 4) / PoseRecordSize;
                if (frameCounts[i] is < 1 or > 4096)
                {
                    valid = false;
                    break;
                }
            }

            if (!valid)
                continue;

            // The final clip is not needed to close the directory and has no next
            // offset.  Leave its frame count unknown rather than guessing where
            // the following resource begins.
            frameCounts[^1] = -1;
            result.Add(new PoseLibraryInfo(header, offsets, frameCounts));
        }

        return result;
    }

    /// <summary>Decode one engine pose frame at an exact ROM file offset.</summary>
    public static PoseFrame ReadPoseFrame(ReadOnlySpan<byte> rom, int offset)
    {
        ValidateRange(rom, offset, PoseRecordSize);
        var parts = new PartPose[GroupCount];
        for (var i = 0; i < parts.Length; i++)
        {
            var at = offset + PoseDataOffset + i * PartPoseSize;
            parts[i] = new PartPose(
                unchecked((sbyte)rom[at]),
                unchecked((sbyte)rom[at + 1]),
                rom[at + 2],
                rom[at + 3],
                rom[at + 4],
                rom[at + 5]);
        }

        return new PoseFrame(
            offset,
            BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset, 2)),
            parts);
    }

    /// <summary>Read a bounded clip frame from a discovered pose directory.</summary>
    public static PoseFrame ReadPoseFrame(
        ReadOnlySpan<byte> rom,
        PoseLibraryInfo library,
        int clipIndex,
        int frameIndex)
    {
        if ((uint)clipIndex >= (uint)library.ClipCount)
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        var frameCount = library.ClipFrameCounts[clipIndex];
        if (frameCount < 0)
            throw new InvalidDataException("The final Downhill Jam pose clip has no bounded end");
        if ((uint)frameIndex >= (uint)frameCount)
            throw new ArgumentOutOfRangeException(nameof(frameIndex));

        return ReadPoseFrame(rom, library.ClipOffsets[clipIndex] + frameIndex * PoseRecordSize);
    }

    public static Vertex[] ReadVertices(ReadOnlySpan<byte> rom, ModelInfo model)
    {
        ValidateRange(rom, model.VertexDataOffset, model.VertexCount * VertexRecordSize);
        var vertices = new Vertex[model.VertexCount];
        for (var i = 0; i < vertices.Length; i++)
        {
            var at = model.VertexDataOffset + i * VertexRecordSize;
            vertices[i] = new Vertex(
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(at, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(at + 2, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(at + 4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(at + 6, 2)));
        }

        return vertices;
    }

    public static Face[] ReadFaces(ReadOnlySpan<byte> rom, ModelInfo model)
    {
        ValidateRange(rom, model.FaceDataOffset, model.FaceCount * FaceRecordSize);
        var faces = new Face[model.FaceCount];
        var face = 0;
        for (var group = 0; group < model.FaceCounts.Length; group++)
        {
            for (var i = 0; i < model.FaceCounts[group]; i++, face++)
            {
                var at = model.FaceDataOffset + face * FaceRecordSize;
                faces[face] = new Face(group, rom[at], rom[at + 1], rom[at + 2], rom[at + 3]);
            }
        }

        return faces;
    }

    private static ModelInfo? TryReadModel(ReadOnlySpan<byte> rom, int header, int index)
    {
        if (header < 0 || header + HeaderSize > rom.Length)
            return null;

        var vertexCounts = ReadCounts(rom, header + 4);
        var faceCounts = ReadCounts(rom, header + FaceCountsOffset);
        if (vertexCounts.Any(static count => count == 0)
            || faceCounts.Any(static count => count == 0))
        {
            return null;
        }

        var vertexCount = vertexCounts.Sum(static count => count);
        var faceCount = faceCounts.Sum(static count => count);
        // Face indices are bytes; real rider records are ~120-150 vertices.  The
        // broad lower bound keeps the content gate useful without baking in this
        // one retail build's exact census.
        if (vertexCount is < 32 or > byte.MaxValue || faceCount is < 16 or > 4096)
            return null;

        var vertexData = header + HeaderSize;
        var faceDataLong = (long)vertexData + (long)vertexCount * VertexRecordSize;
        var endLong = faceDataLong + (long)faceCount * FaceRecordSize;
        if (faceDataLong > int.MaxValue || endLong + 4 > rom.Length)
            return null;
        var faceData = (int)faceDataLong;
        var end = (int)endLong;
        if (BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(end, 4)) != Terminator)
            return null;

        var highest = -1;
        for (var i = 0; i < faceCount; i++)
        {
            var at = faceData + i * FaceRecordSize;
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = rom[at + corner];
                if (vertex >= vertexCount)
                    return null;
                highest = Math.Max(highest, vertex);
            }
        }

        if (highest + 1 != vertexCount)
            return null;

        return new ModelInfo(
            index, header, vertexCounts, faceCounts, vertexData, faceData, end + 4);
    }

    private static ushort[] ReadCounts(ReadOnlySpan<byte> rom, int offset)
    {
        var result = new ushort[GroupCount];
        for (var i = 0; i < result.Length; i++)
            result[i] = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + i * 2, 2));
        return result;
    }

    private static bool IsDownhillJam(ReadOnlySpan<byte> rom) =>
        rom.Length >= 0xB0
        && rom[0xAC] == (byte)'B'
        && rom[0xAD] == (byte)'X'
        && rom[0xAE] == (byte)'S';

    private static void ValidateRange(ReadOnlySpan<byte> rom, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > rom.Length - length)
            throw new InvalidDataException("Downhill Jam model points outside the ROM");
    }
}
