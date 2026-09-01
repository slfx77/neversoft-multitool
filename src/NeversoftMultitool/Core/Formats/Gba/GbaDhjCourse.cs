using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Visual Impact's polygonal course container used by Tony Hawk's Downhill
///     Jam on GBA.  It is unrelated to the isometric tile/collision containers
///     used by the Vicarious Visions games.
///
///     <para>All pointers in the 0x20-byte header are relative to the header.
///     The first two sections are a six-byte signed XYZ vertex bank and a
///     fourteen-byte indexed triangle bank.  The 0x30-byte records between the
///     header and vertex bank provide the engine's streamed course ranges and
///     four packed references into the collision-polyline pool.</para>
///
///     <para>Discovery deliberately does not use retail ROM offsets.  A candidate
///     must close its chunk, vertex, triangle, centre-line, collision, object and
///     edge sections, and its immediately preceding texture resource must end at
///     the course header exactly.</para>
/// </summary>
public static class GbaDhjCourse
{
    public const int HeaderSize = 0x20;
    public const int ChunkRecordSize = 0x30;
    public const int VertexRecordSize = 6;
    public const int FaceRecordSize = 14;
    public const int ObjectRecordSize = 16;
    public const int CollisionPointRecordSize = 8;
    public const int TextureHeaderSize = 0x208;
    public const int TexturePageSize = 128 * 128;
    public const int TexturePageDimension = 128;
    public const int PageZeroDimension = 64;
    public const int PaletteColourCount = 240;

    private const int MaximumRelativeSection = 0x800000;
    private const int MaximumTexturePages = 32;
    private const uint MissingCollisionReference = uint.MaxValue;

    public sealed record CourseInfo(
        int Index,
        int HeaderOffset,
        int EndOffset,
        int TextureOffset,
        int TexturePageCount,
        int ChunkCount,
        int VertexDataOffset,
        int VertexCount,
        int FaceDataOffset,
        int FaceCount,
        int CentreLineOffset,
        int CentreLinePointCount,
        int CollisionPoolOffset,
        int ObjectDataOffset,
        int ObjectCount,
        int LeftEdgeOffset,
        int LeftEdgePointCount,
        int RightEdgeOffset,
        int RightEdgePointCount)
    {
        /// <summary>
        ///     The table has one look-ahead record beyond the course-section count.
        ///     That record is often a terminator, but carries the final mesh range
        ///     on courses with an alternate branch.
        /// </summary>
        public int ChunkRecordCount => ChunkCount + 1;
    }

    public readonly record struct Vertex(short X, short Y, short Z);

    /// <summary>
    ///     UV words store U in the low byte and V in the high byte.  The low six
    ///     material bits select a texture page; 63 selects a flat palette colour
    ///     whose index is <c>Material &gt;&gt; 9</c>.
    /// </summary>
    public readonly record struct Face(
        ushort V0,
        ushort V1,
        ushort V2,
        ushort Uv0,
        ushort Uv1,
        ushort Uv2,
        ushort Material)
    {
        public int TexturePage => Material & 0x3F;
        public bool IsFlatColour => TexturePage == 0x3F;
        public int PaletteIndex => Material >> 9;
    }

    public readonly record struct EdgePoint(short X, short Y, short Z);

    /// <summary>
    ///     Centre-line coordinates are stored in the renderer's Y/Z/X order,
    ///     unlike mesh, edge and collision vertices.  For example, the first live
    ///     course's (6,-805,1) centre point is the midpoint of edge points
    ///     (-37,6,-805) and (40,6,-805).  Naming the fields explicitly prevents
    ///     consumers from silently treating the first value as lateral X.
    /// </summary>
    public readonly record struct CentrePoint(int Y, int Z, int X);

    public readonly record struct CollisionPoint(short Meta, short X, short Y, short Z);

    public sealed record CollisionPolyline(int PoolHalfwordOffset, CollisionPoint[] Points);

    public sealed record TexturePage(int Index, int Width, int Height, byte[] Rgba);

    /// <summary>Find every structurally closed course in a Downhill Jam ROM.</summary>
    public static IReadOnlyList<CourseInfo> FindCourses(ReadOnlySpan<byte> rom)
    {
        if (!IsDownhillJam(rom))
            return [];

        var result = new List<CourseInfo>();
        for (var header = 0; header <= rom.Length - HeaderSize; header += 4)
        {
            // The vertex section follows an exact number of 0x30-byte records.
            // This cheap gate rejects almost every word in the ROM before any
            // larger section is inspected.
            var vertexRelative = ReadU32(rom, header);
            if (vertexRelative <= HeaderSize
                || vertexRelative > 0x20000
                || (vertexRelative - HeaderSize) % ChunkRecordSize != 0)
            {
                continue;
            }

            try
            {
                if (TryReadCourse(rom, header, result.Count) is { } course)
                    result.Add(course);
            }
            catch (Exception ex) when (ex is ArgumentException
                                       or ArithmeticException
                                       or InvalidDataException)
            {
                // A BXS-tagged malformed/truncated input can imitate the cheap
                // header gate. Discovery is a probe, so reject that candidate
                // instead of leaking Span slicing/checked-arithmetic exceptions.
            }
        }

        return result;
    }

    public static Vertex[] ReadVertices(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        var start = ValidateRangeAndGetOffset(
            rom, course.VertexDataOffset, (long)course.VertexCount * VertexRecordSize);
        var result = new Vertex[course.VertexCount];
        for (var i = 0; i < result.Length; i++)
        {
            var at = start + i * VertexRecordSize;
            result[i] = new Vertex(ReadS16(rom, at), ReadS16(rom, at + 2), ReadS16(rom, at + 4));
        }

        return result;
    }

    public static Face[] ReadFaces(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        var start = ValidateRangeAndGetOffset(
            rom, course.FaceDataOffset, (long)course.FaceCount * FaceRecordSize);
        var result = new Face[course.FaceCount];
        for (var i = 0; i < result.Length; i++)
        {
            var at = start + i * FaceRecordSize;
            result[i] = new Face(
                ReadU16(rom, at), ReadU16(rom, at + 2), ReadU16(rom, at + 4),
                ReadU16(rom, at + 6), ReadU16(rom, at + 8), ReadU16(rom, at + 10),
                ReadU16(rom, at + 12));
        }

        return result;
    }

    public static CentrePoint[] ReadCentreLine(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        var start = ValidateRangeAndGetOffset(
            rom, (long)course.CentreLineOffset + 4, (long)course.CentreLinePointCount * 12);
        var result = new CentrePoint[course.CentreLinePointCount];
        for (var i = 0; i < result.Length; i++)
        {
            var at = start + i * 12;
            result[i] = new CentrePoint(ReadS32(rom, at), ReadS32(rom, at + 4), ReadS32(rom, at + 8));
        }

        return result;
    }

    public static EdgePoint[] ReadLeftEdge(ReadOnlySpan<byte> rom, CourseInfo course) =>
        ReadEdge(rom, course.LeftEdgeOffset, course.LeftEdgePointCount);

    /// <summary>
    ///     Returns the second road edge.  The shipped bonus/test course ends in
    ///     the engine's 0xCDCD terminator instead, for which this returns empty.
    /// </summary>
    public static EdgePoint[] ReadRightEdge(ReadOnlySpan<byte> rom, CourseInfo course) =>
        ReadEdge(rom, course.RightEdgeOffset, course.RightEdgePointCount);

    /// <summary>
    ///     Resolve all unique collision lists referenced by chunk fields
    ///     +0x10/+0x14/+0x18/+0x1C.  The packed low 24 bits are a halfword offset
    ///     into the pool and the high byte is the engine's nearby segment cursor;
    ///     connectivity is the sequential order of each returned point array.
    /// </summary>
    public static CollisionPolyline[] ReadCollisionPolylines(
        ReadOnlySpan<byte> rom,
        CourseInfo course)
    {
        var references = ReadCollisionReferenceOffsets(rom, course);
        var result = new CollisionPolyline[references.Length];
        for (var list = 0; list < references.Length; list++)
        {
            var relative = references[list];
            // Validate the count word before reading it; CourseInfo is public and
            // callers are not required to have obtained it from FindCourses.
            var at = ValidateRangeAndGetOffset(
                rom, (long)course.CollisionPoolOffset + relative * 2L, 2);
            var count = ReadU16(rom, at);
            var pointsStart = ValidateRangeAndGetOffset(
                rom, (long)at + 2, (long)count * CollisionPointRecordSize);
            var points = new CollisionPoint[count];
            for (var i = 0; i < points.Length; i++)
            {
                var point = pointsStart + i * CollisionPointRecordSize;
                points[i] = new CollisionPoint(
                    ReadS16(rom, point), ReadS16(rom, point + 2),
                    ReadS16(rom, point + 4), ReadS16(rom, point + 6));
            }

            result[list] = new CollisionPolyline(relative, points);
        }

        return result;
    }

    /// <summary>Decode the course's 240-entry BGR555 palette to RGBA.</summary>
    public static byte[] ReadPaletteRgba(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        const int paletteOffset = 8;
        var start = ValidateRangeAndGetOffset(
            rom, (long)course.TextureOffset + paletteOffset, PaletteColourCount * 2L);
        var result = new byte[PaletteColourCount * 4];
        for (var i = 0; i < PaletteColourCount; i++)
        {
            var colour = ReadU16(rom, start + i * 2);
            result[i * 4] = Expand5(colour & 0x1F);
            result[i * 4 + 1] = Expand5((colour >> 5) & 0x1F);
            result[i * 4 + 2] = Expand5((colour >> 10) & 0x1F);
            // Palette zero is the conspicuous 0x7C1F magenta chroma key used by
            // the software renderer.  Flat faces never select it.
            result[i * 4 + 3] = i == 0 ? (byte)0 : (byte)255;
        }

        return result;
    }

    /// <summary>
    ///     Decode one texture page.  The engine downsamples page zero by taking
    ///     every other source texel into a 64x64 buffer; other pages remain raw
    ///     128x128 indexed images.
    /// </summary>
    public static TexturePage ReadTexturePage(
        ReadOnlySpan<byte> rom,
        CourseInfo course,
        int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= course.TexturePageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));

        var source = ValidateRangeAndGetOffset(
            rom,
            (long)course.TextureOffset + TextureHeaderSize + (long)pageIndex * TexturePageSize,
            TexturePageSize);
        var palette = ReadPaletteRgba(rom, course);
        var dimension = pageIndex == 0 ? PageZeroDimension : TexturePageDimension;
        var step = pageIndex == 0 ? 2 : 1;
        var rgba = new byte[dimension * dimension * 4];
        for (var y = 0; y < dimension; y++)
        for (var x = 0; x < dimension; x++)
        {
            var paletteIndex = rom[source + y * step * TexturePageDimension + x * step];
            if (paletteIndex >= PaletteColourCount)
                throw new InvalidDataException("Downhill Jam texture references an absent palette colour");
            palette.AsSpan(paletteIndex * 4, 4).CopyTo(rgba.AsSpan((y * dimension + x) * 4, 4));
        }

        return new TexturePage(pageIndex, dimension, dimension, rgba);
    }

    private static CourseInfo? TryReadCourse(ReadOnlySpan<byte> rom, int header, int index)
    {
        var vertexRelative = ReadU32(rom, header);
        var faceRelative = ReadU32(rom, header + 4);
        var centreRelative = ReadU32(rom, header + 8);
        var objectCountValue = ReadU32(rom, header + 0xC);
        var leftRelative = ReadU32(rom, header + 0x10);
        var rightRelative = ReadU32(rom, header + 0x14);
        var collisionRelative = ReadU32(rom, header + 0x18);
        var objectRelative = ReadU32(rom, header + 0x1C);

        if (faceRelative <= vertexRelative
            || centreRelative <= faceRelative
            || collisionRelative <= centreRelative
            || objectRelative <= collisionRelative
            || leftRelative <= objectRelative
            || rightRelative <= leftRelative
            || rightRelative > MaximumRelativeSection
            || objectCountValue is 0 or > 4096)
        {
            return null;
        }

        if (!TryAbsolute(header, vertexRelative, rom.Length, out var vertex)
            || !TryAbsolute(header, faceRelative, rom.Length, out var face)
            || !TryAbsolute(header, centreRelative, rom.Length, out var centre)
            || !TryAbsolute(header, collisionRelative, rom.Length, out var collision)
            || !TryAbsolute(header, objectRelative, rom.Length, out var objects)
            || !TryAbsolute(header, leftRelative, rom.Length, out var left)
            || !TryAbsolute(header, rightRelative, rom.Length, out var right))
        {
            return null;
        }

        var chunkRecordCount = (int)((vertexRelative - HeaderSize) / ChunkRecordSize);
        if (chunkRecordCount is < 33 or > 4096)
            return null;

        if (centre > rom.Length - 4)
            return null;
        var centreCountValue = ReadU32(rom, centre);
        if (centreCountValue > int.MaxValue || chunkRecordCount != (int)centreCountValue + 1)
            return null;
        var chunkCount = (int)centreCountValue;

        var vertexBytes = faceRelative - vertexRelative;
        var faceBytes = centreRelative - faceRelative;
        var vertexPadding = vertexBytes % VertexRecordSize;
        var facePadding = faceBytes % FaceRecordSize;
        if (vertexPadding > 2 || facePadding > 2)
            return null;
        var vertexCount = (int)(vertexBytes / VertexRecordSize);
        var faceCount = (int)(faceBytes / FaceRecordSize);
        if (vertexCount is < 3 or > ushort.MaxValue || faceCount is < 1 or > 200_000)
            return null;

        var centrePointCount = chunkCount + 1;
        var centreEnd = (long)centre + 4L + centrePointCount * 12L;
        if (centreEnd > collision)
            return null;

        var objectCount = (int)objectCountValue;
        if ((long)objects + objectCount * ObjectRecordSize != left)
            return null;

        var leftCount = ReadU16(rom, left);
        if (leftCount < 2 || (long)left + 2L + leftCount * 6L != right)
            return null;

        var rightCountValue = ReadU16(rom, right);
        int rightCount;
        int end;
        if (rightCountValue is >= 2 and <= 4096
            && (long)right + 2L + rightCountValue * 6L <= rom.Length)
        {
            rightCount = rightCountValue;
            end = checked(right + 2 + rightCount * 6);
        }
        else if (rightCountValue == 0xCDCD)
        {
            // The final retail course has one authored road edge and terminates
            // this resource with the engine's two-byte debug fill marker.
            rightCount = 0;
            end = right + 2;
        }
        else
        {
            return null;
        }

        if (!ValidateChunkRanges(rom, header, chunkRecordCount, vertexCount, faceCount)
            || !ValidateFaces(rom, face, faceCount, vertexCount, out var highestVertex,
                out var highestTexturePage)
            || highestVertex + 1 != vertexCount)
        {
            return null;
        }

        if (!TryFindTexture(rom, header, highestTexturePage, out var texture, out var pages))
            return null;

        var provisional = new CourseInfo(
            index, header, end, texture, pages, chunkCount,
            vertex, vertexCount, face, faceCount, centre, centrePointCount,
            collision, objects, objectCount, left, leftCount, right, rightCount);
        if (!ValidateCollisionPool(rom, provisional))
            return null;

        return provisional;
    }

    private static bool ValidateChunkRanges(
        ReadOnlySpan<byte> rom,
        int header,
        int recordCount,
        int vertexCount,
        int faceCount)
    {
        var highestVertex = -1;
        var highestFace = -1;
        for (var i = 0; i < recordCount; i++)
        {
            var at = header + HeaderSize + i * ChunkRecordSize;
            var vertexStart = ReadU16(rom, at);
            var vertexEnd = ReadU16(rom, at + 2);
            var faceStart = ReadU16(rom, at + 4);
            var faceEnd = ReadU16(rom, at + 6);

            // 0x7FFF/0 is the empty look-ahead record used on several courses.
            var emptyVertices = vertexStart == 0x7FFF && vertexEnd == 0;
            if (!emptyVertices && (vertexStart > vertexEnd || vertexEnd >= vertexCount))
                return false;
            if (faceStart > faceEnd || faceEnd > faceCount)
                return false;

            if (!emptyVertices)
                highestVertex = Math.Max(highestVertex, vertexEnd);
            highestFace = Math.Max(highestFace, faceEnd);
        }

        return highestVertex + 1 == vertexCount && highestFace == faceCount;
    }

    private static bool ValidateFaces(
        ReadOnlySpan<byte> rom,
        int face,
        int faceCount,
        int vertexCount,
        out int highestVertex,
        out int highestTexturePage)
    {
        highestVertex = -1;
        highestTexturePage = -1;
        for (var i = 0; i < faceCount; i++)
        {
            var at = face + i * FaceRecordSize;
            for (var corner = 0; corner < 3; corner++)
            {
                var vertex = ReadU16(rom, at + corner * 2);
                if (vertex >= vertexCount)
                    return false;
                highestVertex = Math.Max(highestVertex, vertex);
            }

            var material = ReadU16(rom, at + 12);
            var page = material & 0x3F;
            if (page != 0x3F)
                highestTexturePage = Math.Max(highestTexturePage, page);
        }

        return true;
    }

    private static bool TryFindTexture(
        ReadOnlySpan<byte> rom,
        int courseHeader,
        int highestTexturePage,
        out int texture,
        out int pages)
    {
        texture = 0;
        pages = 0;
        for (var candidatePages = Math.Max(1, highestTexturePage + 1);
             candidatePages <= MaximumTexturePages;
             candidatePages++)
        {
            var bytes = TextureHeaderSize + candidatePages * TexturePageSize;
            var candidate = courseHeader - bytes;
            if (candidate < 0
                || ReadU16(rom, candidate) != candidatePages
                || ReadU16(rom, candidate + 2) != TexturePageDimension
                || ReadU16(rom, candidate + 4) != 0
                || ReadU16(rom, candidate + 6) != 0x45)
            {
                continue;
            }

            // Indexed pages have a 240-colour palette.  This also catches a
            // random lookalike whose four-word prefix happens to match.
            var pixelStart = candidate + TextureHeaderSize;
            var pixelEnd = courseHeader;
            var valid = true;
            for (var at = pixelStart; at < pixelEnd; at++)
                if (rom[at] >= PaletteColourCount)
                {
                    valid = false;
                    break;
                }
            if (!valid)
                continue;

            if (pages != 0)
                return false; // a valid course must have one unambiguous companion
            texture = candidate;
            pages = candidatePages;
        }

        return pages != 0;
    }

    private static bool ValidateCollisionPool(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        int[] references;
        try
        {
            references = ReadCollisionReferenceOffsets(rom, course);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (references.Length == 0 || references[0] != 0)
            return false;

        var expected = course.CollisionPoolOffset;
        foreach (var relative in references)
        {
            var atLong = (long)course.CollisionPoolOffset + relative * 2L;
            if (atLong != expected || atLong < course.CollisionPoolOffset || atLong > course.ObjectDataOffset - 2)
                return false;
            var at = (int)atLong;
            var count = ReadU16(rom, at);
            if (count < 2)
                return false;
            var end = (long)at + 2L + count * CollisionPointRecordSize;
            if (end > course.ObjectDataOffset)
                return false;
            expected = (int)end;
        }

        // Some pools have one alignment halfword between their last list and
        // the 16-byte object bank; no other gap is accepted.
        return expected == course.ObjectDataOffset || expected + 2 == course.ObjectDataOffset;
    }

    private static int[] ReadCollisionReferenceOffsets(ReadOnlySpan<byte> rom, CourseInfo course)
    {
        var recordCountValue = (long)course.ChunkCount + 1;
        var table = ValidateRangeAndGetOffset(
            rom,
            (long)course.HeaderOffset + HeaderSize,
            recordCountValue * ChunkRecordSize);
        var recordCount = checked((int)recordCountValue);
        var references = new SortedSet<int>();
        for (var chunk = 0; chunk < recordCount; chunk++)
        {
            var record = table + chunk * ChunkRecordSize;
            for (var field = 0x10; field <= 0x1C; field += 4)
            {
                var packed = ReadU32(rom, record + field);
                if (packed == MissingCollisionReference)
                    continue;
                references.Add((int)(packed & 0x00FF_FFFF));
            }
        }

        return references.ToArray();
    }

    private static EdgePoint[] ReadEdge(ReadOnlySpan<byte> rom, int offset, int count)
    {
        if (count == 0)
            return [];
        var start = ValidateRangeAndGetOffset(rom, (long)offset + 2, (long)count * 6);
        var result = new EdgePoint[count];
        for (var i = 0; i < count; i++)
        {
            var at = start + i * 6;
            result[i] = new EdgePoint(ReadS16(rom, at), ReadS16(rom, at + 2), ReadS16(rom, at + 4));
        }

        return result;
    }

    private static bool IsDownhillJam(ReadOnlySpan<byte> rom) =>
        rom.Length >= 0xB0
        && rom[0xAC] == (byte)'B'
        && rom[0xAD] == (byte)'X'
        && rom[0xAE] == (byte)'S';

    private static bool TryAbsolute(int header, uint relative, int length, out int absolute)
    {
        var value = (long)header + relative;
        absolute = value is >= 0 and < int.MaxValue ? (int)value : -1;
        return value >= 0 && value <= length - 2;
    }

    private static int ValidateRangeAndGetOffset(ReadOnlySpan<byte> rom, long offset, long length)
    {
        if (offset < 0 || length < 0 || offset > rom.Length || length > rom.Length - offset)
            throw new InvalidDataException("Downhill Jam course points outside the ROM");
        return (int)offset;
    }

    private static byte Expand5(int value) => (byte)((value << 3) | (value >> 2));

    private static short ReadS16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));

    private static int ReadS32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));

    private static ushort ReadU16(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));

    private static uint ReadU32(ReadOnlySpan<byte> data, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
}
