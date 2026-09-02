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
/// +0x84 Vertex[sum(vertexCount)]       // s16 x,y,z; u8 texU, u8 texV
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

    /// <summary>
    ///     Edge of the 8bpp texture page a vertex's <see cref="Vertex.U" />/
    ///     <see cref="Vertex.V" /> index into, and the row stride the fetch uses.
    ///     This is the page geometry, not a bound the data guarantees — see the
    ///     out-of-range note on <see cref="Vertex" />.
    /// </summary>
    public const int TexturePageSize = 32;

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

    /// <summary>
    ///     One authored vertex.  <paramref name="PackedTexCoord" /> is the u16 at
    ///     record offset +6: its low byte (+6) is <see cref="U" /> and its high byte
    ///     (+7) is <see cref="V" />, addressing an 8bpp texture page as
    ///     <c>page[V * TexturePageSize + U]</c>.
    ///
    ///     <para><b>Why this is a texture coordinate and not a per-vertex normal</b>
    ///     (it was previously read as one).  Model 19's group 0 is the skateboard
    ///     deck: six vertices on a single plane — least-squares residual 2.98 units
    ///     over a 126x23-unit footprint, tilted 2.6 degrees — so the whole group has
    ///     one geometric normal.  Yet all six stored values are distinct and vary
    ///     linearly with position: V tracks length x (-59, -46/-45, 51, 67 give
    ///     0, 4, 27, 31) and U tracks width y (-13/-11, -1/0, +10 give 19, 25, 31),
    ///     with equal coordinates giving equal values and both centreline apex
    ///     vertices taking U=25, the exact midpoint of that 19..31 strip.  A normal
    ///     cannot vary across a flat plane, and a texture coordinate must.  The
    ///     engine agrees twice over: the rider's 13-part transform copies this field
    ///     VERBATIM without rotating it (a per-vertex normal on a rigid part would
    ///     have to rotate), and the level renderer injects the same u16 per corner
    ///     from a 14-byte face record, after which the rasterizer splits it into low
    ///     and high bytes, builds affine screen-space gradients from them and
    ///     fetches a texel.  Read instead as a normal, 44 candidate decodes score a
    ///     74.54-degree mean error against the face normals versus 77.44 degrees for
    ///     a shuffled control — no better than chance.</para>
    ///
    ///     <para>Values are NOT clamped to the page.  3,218 of the 3,248 vertices in
    ///     the retail US image's 24 rider variants fall inside the 0..31 box; the
    ///     remaining 30 are two repeated literals, (61,63) and (16,32), whose
    ///     meaning is not decoded — they are exported as authored rather than
    ///     folded into range.</para>
    /// </summary>
    public readonly record struct Vertex(short X, short Y, short Z, ushort PackedTexCoord)
    {
        /// <summary>Column into the texture page; record byte +6.</summary>
        public byte U => (byte)(PackedTexCoord & 0xFF);

        /// <summary>Row into the texture page; record byte +7.</summary>
        public byte V => (byte)(PackedTexCoord >> 8);
    }

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
    ///     into the directory header, and each clip is <b>prefixed</b> by a
    ///     <c>u32</c> stating its own frame count — the directory's offsets point
    ///     just past that word, so a clip is
    ///     <c>u32 frameCount</c> followed by <c>frameCount</c> <c>0x50</c>-byte
    ///     pose records.
    ///
    ///     <para>The word was previously read as a trailing playback value
    ///     belonging to the clip in front of it.  It is not: on the retail US image
    ///     the <c>u32</c> at <c>ClipOffsets[i] - 4</c> equals clip <i>i</i>'s
    ///     offset-derived frame count for all 93 bounded clips, whereas read as
    ///     clip <i>i</i>'s trailer it matches only 11 of 93 (chance) and matches the
    ///     <i>next</i> clip's count 92 out of 92 — i.e. what looked like a trailer
    ///     was always the following clip's prefix.  Clip 0 settles it on its own:
    ///     its prefix sits at <c>0x00E71990</c>, exactly where the 94-entry offset
    ///     table ends, with no preceding clip to own it.</para>
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

            // Every clip STATES its own frame count in the u32 immediately before
            // its offset (see PoseLibraryInfo).  A clip that has a following
            // directory offset is also bounded by it, and the two readings must
            // AGREE: that corpus-wide agreement across every bounded clip is what
            // makes the final clip's otherwise unbounded prefix trustworthy, so a
            // single disagreement rejects the whole candidate directory.
            var frameCounts = new int[clipCount];
            for (var i = 0; i < clipCount; i++)
            {
                if (offsets[i] < 4)
                {
                    valid = false;
                    break;
                }

                var stated = BinaryPrimitives.ReadUInt32LittleEndian(
                    rom.Slice(offsets[i] - 4, 4));
                if (stated is < 1 or > 4096
                    || (long)offsets[i] + (long)stated * PoseRecordSize > rom.Length)
                {
                    valid = false;
                    break;
                }

                if (i < clipCount - 1)
                {
                    // The span up to the next clip's offset covers this clip's
                    // records plus that clip's own 4-byte prefix.
                    var length = offsets[i + 1] - offsets[i];
                    if (length < PoseRecordSize + 4
                        || (length - 4) % PoseRecordSize != 0
                        || (length - 4) / PoseRecordSize != stated)
                    {
                        valid = false;
                        break;
                    }
                }

                frameCounts[i] = (int)stated;
            }

            if (!valid)
                continue;

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
        // FindPoseLibraries only ever reports a positive stated frame count, so
        // this rejects a hand-built PoseLibraryInfo rather than a discovered one —
        // a caller that synthesises a clip with no decoded length still fails
        // closed instead of reading pose records out of unrelated ROM data.
        var frameCount = library.ClipFrameCounts[clipIndex];
        if (frameCount < 1)
            throw new InvalidDataException("The Downhill Jam pose clip has no decoded frame count");
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
