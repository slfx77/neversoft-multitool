using System.Buffers.Binary;
using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>
///     Parses Neversoft collision (.col) files for THUG/THUG2/THAW.
///     Binary format (version 10): file header (32B) + per-object headers (64B each)
///     + vertex data (fixed 6B or float 12B) + intensity data (1B per vert) + face data.
///     Vertices: fixed-point 3×u16 (×0.0625 + bbox_min) or float 3×f32.
///     Faces: small (u16 flags + u16 terrain + 3×u8 indices + pad) or large (+ 3×u16 indices).
///     Reference: io_thps_scene import_thug2.py (denetii/io_thps_scene).
/// </summary>
public static class ColFile
{
    private const int SizeofHeader = 32; // 8 × i32
    private const int SizeofObject = 64; // per-object header
    private const int SizeofFloatVert = 12; // 3 × f32
    private const int SizeofFixedVert = 6; // 3 × u16
    private const int SizeofSmallFace = 8; // flags:u16 + terrain:u16 + 3×u8 + pad:u8
    private const int SizeofLargeFace = 10; // flags:u16 + terrain:u16 + 3×u16

    /// <summary>Returns true if the data looks like a valid COL file (version 9 or 10).</summary>
    public static bool IsColFile(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader) return false;
        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        return version is 9 or 10;
    }

    public static ColScene Parse(string filePath)
    {
        return Parse(File.ReadAllBytes(filePath));
    }

    public static ColScene Parse(byte[] data)
    {
        return Parse(data.AsSpan());
    }

    public static ColScene Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < SizeofHeader)
            throw new InvalidDataException("File too small for COL header");

        // ── File header ──
        var version = BinaryPrimitives.ReadInt32LittleEndian(data);
        if (version is not (9 or 10))
            throw new InvalidDataException($"Unsupported COL version: {version}");

        var numObjects = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        var totalVerts = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var totalLargeFaces = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
        // totalSmallFaces @ 16 (not needed)
        var totalLargeVerts = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        var totalSmallVerts = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);

        if (numObjects < 0 || numObjects > 100_000)
            throw new InvalidDataException($"Unreasonable object count: {numObjects}");

        // ── Offset calculations ──
        var baseVertOffset = Align16(SizeofHeader + SizeofObject * numObjects);
        var baseIntensityOffset = baseVertOffset +
                                  totalLargeVerts * SizeofFloatVert +
                                  totalSmallVerts * SizeofFixedVert;
        var baseFaceOffset = Align4(baseIntensityOffset + totalVerts);

        // ── Parse per-object headers + geometry ──
        var objects = new ColObject[numObjects];
        var headerOffset = SizeofHeader;

        for (var i = 0; i < numObjects; i++)
        {
            objects[i] = ParseObject(data, headerOffset, baseVertOffset, baseIntensityOffset, baseFaceOffset);
            headerOffset += SizeofObject;
        }

        return new ColScene
        {
            Version = version,
            Objects = objects
        };
    }

    private static ColObject ParseObject(
        ReadOnlySpan<byte> data, int hdr,
        int baseVertOffset, int baseIntensityOffset, int baseFaceOffset)
    {
        var checksum = BinaryPrimitives.ReadUInt32LittleEndian(data[hdr..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 4)..]);
        var numVerts = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 6)..]);
        var numFaces = BinaryPrimitives.ReadUInt16LittleEndian(data[(hdr + 8)..]);
        var useSmallFaces = data[hdr + 10] != 0;
        var useFixed = data[hdr + 11] != 0;
        var firstFaceOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 12)..]);

        var bboxMin = new Vector3(
            BitConverter.ToSingle(data[(hdr + 16)..]),
            BitConverter.ToSingle(data[(hdr + 20)..]),
            BitConverter.ToSingle(data[(hdr + 24)..])
        );
        var bboxMax = new Vector3(
            BitConverter.ToSingle(data[(hdr + 32)..]),
            BitConverter.ToSingle(data[(hdr + 36)..]),
            BitConverter.ToSingle(data[(hdr + 40)..])
        );

        var firstVertOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 48)..]);
        var intensityOffset = BinaryPrimitives.ReadInt32LittleEndian(data[(hdr + 56)..]);

        // ── Vertices ──
        var vertices = new Vector3[numVerts];
        var absVertOffset = baseVertOffset + firstVertOffset;

        if (useFixed)
        {
            for (var v = 0; v < numVerts; v++)
            {
                var off = absVertOffset + v * SizeofFixedVert;
                if (off + SizeofFixedVert > data.Length) break;
                var rx = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var ry = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                var rz = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
                vertices[v] = new Vector3(
                    rx * 0.0625f + bboxMin.X,
                    ry * 0.0625f + bboxMin.Y,
                    rz * 0.0625f + bboxMin.Z
                );
            }
        }
        else
        {
            for (var v = 0; v < numVerts; v++)
            {
                var off = absVertOffset + v * SizeofFloatVert;
                if (off + SizeofFloatVert > data.Length) break;
                vertices[v] = new Vector3(
                    BitConverter.ToSingle(data[off..]),
                    BitConverter.ToSingle(data[(off + 4)..]),
                    BitConverter.ToSingle(data[(off + 8)..])
                );
            }
        }

        // ── Intensities ──
        var intensities = new byte[numVerts];
        if (intensityOffset >= 0)
        {
            var absIntensityOffset = baseIntensityOffset + intensityOffset;
            for (var v = 0; v < numVerts && absIntensityOffset + v < data.Length; v++)
                intensities[v] = data[absIntensityOffset + v];
        }

        // ── Faces ──
        var faces = new ColFace[numFaces];
        var absFaceOffset = baseFaceOffset + firstFaceOffset;

        if (useSmallFaces)
        {
            for (var f = 0; f < numFaces; f++)
            {
                var off = absFaceOffset + f * SizeofSmallFace;
                if (off + SizeofSmallFace > data.Length) break;
                var faceFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var terrain = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                faces[f] = new ColFace(faceFlags, terrain, data[off + 4], data[off + 5], data[off + 6]);
            }
        }
        else
        {
            for (var f = 0; f < numFaces; f++)
            {
                var off = absFaceOffset + f * SizeofLargeFace;
                if (off + SizeofLargeFace > data.Length) break;
                var faceFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[off..]);
                var terrain = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 2)..]);
                var v0 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 4)..]);
                var v1 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 6)..]);
                var v2 = BinaryPrimitives.ReadUInt16LittleEndian(data[(off + 8)..]);
                faces[f] = new ColFace(faceFlags, terrain, v0, v1, v2);
            }
        }

        return new ColObject
        {
            Checksum = checksum,
            Flags = flags,
            BBoxMin = bboxMin,
            BBoxMax = bboxMax,
            Vertices = vertices,
            Faces = faces,
            Intensities = intensities
        };
    }

    private static int Align16(int value)
    {
        return (value + 15) & ~15;
    }

    private static int Align4(int value)
    {
        return (value + 3) & ~3;
    }
}
