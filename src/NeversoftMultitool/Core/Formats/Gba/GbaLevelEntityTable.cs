using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     One 16-byte record from a level's entity table.
/// </summary>
/// <remarks>
///     Only two fields are established. <see cref="WorldX" /> and <see cref="WorldY" />
///     are world coordinates at 48 raw units per collision cell — every record in the
///     corpus lands inside its own level's grid, across grids from 9x15 to 56x35, with
///     the maximum always just under the edge. Everything else is named by index on
///     purpose:
///     <list type="bullet">
///         <item>
///             <c>Field2</c> is signed and takes negative values, so it is a
///             coordinate rather than a size — but which one, and in what unit, is not
///             established. The obvious test ("the object's base rests on the collision
///             surface") failed against its own shuffled-ground control at every scale
///             and anchor tried.
///         </item>
///         <item>
///             <c>Field3</c>/<c>Field4</c>/<c>Field5</c> are unread. Two shapes
///             survive — a box size, or a second point — and nothing decides between
///             them.
///         </item>
///         <item>
///             <c>Field6</c> is an id banded on decimal thousands.
///         </item>
///         <item>
///             <c>Field7</c> is always a multiple of 0x1000 and takes very few
///             distinct values, which is the shape of a quantized orientation. It is
///             not named one here, because nothing has confirmed it.
///         </item>
///     </list>
/// </remarks>
public readonly record struct GbaLevelEntity(
    int WorldX,
    int WorldY,
    int Field2,
    int Field3,
    int Field4,
    int Field5,
    int Field6,
    int Field7)
{
    /// <summary>The collision cell this record sits in.</summary>
    public int CellX => WorldX / GbaLevelEntityTable.RawUnitsPerCell;

    /// <inheritdoc cref="CellX" />
    public int CellY => WorldY / GbaLevelEntityTable.RawUnitsPerCell;
}

/// <summary>
///     The per-level entity table at level-record <c>+0x150</c>: a <c>u32</c> count
///     followed by that many 16-byte records.
/// </summary>
/// <remarks>
///     Found while establishing that a level's rails are NOT in the collision
///     heightfield — that grid's cell is three world units, and a handrail is a pipe a
///     few centimetres across, so it cannot be represented there at all. This table is
///     where the placed content lives. It is READ but NOT DECODED: see
///     <see cref="GbaLevelEntity" /> for exactly which fields are established.
///     <para>
///         No geometry is emitted from it. A fitted layout that draws a convincing
///         picture is not a decode — joining each id pair as a line puts a segment
///         straight down School II's staircase handrail, which looks like the answer
///         and is not, because that staircase has two parallel rails and joining one
///         end of each draws the same line.
///     </para>
/// </remarks>
public static class GbaLevelEntityTable
{
    /// <summary>The table pointer's offset inside the level record.</summary>
    public const int TableField = 0x150;

    /// <summary>Bytes per entity record.</summary>
    public const int RecordBytes = 16;

    /// <summary>
    ///     Raw units per collision cell in the entity table's coordinate space. A
    ///     cell spans three world units, so this is 16 raw units per world unit.
    /// </summary>
    public const int RawUnitsPerCell = 48;

    private const uint RomBase = 0x08000000;

    /// <summary>
    ///     The loader addresses a level record 0x144 bytes before the address the art
    ///     scanner reports, so the two views of the table differ by this constant.
    /// </summary>
    private const int ScanRecordDelta = 0x144;

    private const int LevelRecordStride = GbaLevelCarver.LevelRecordSize;

    /// <summary>
    ///     Read one level's entity table, or null when the record's pointer or count
    ///     does not resolve inside the ROM.
    /// </summary>
    public static IReadOnlyList<GbaLevelEntity>? TryRead(ReadOnlySpan<byte> rom, int trueRecordOffset)
    {
        if (trueRecordOffset < 0 || trueRecordOffset + TableField + 4 > rom.Length)
            return null;

        var pointer = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecordOffset + TableField, 4));
        if (pointer < RomBase || pointer - RomBase > (uint)rom.Length)
            return null;

        var at = (int)(pointer - RomBase);
        if (at + 4 > rom.Length) return null;

        var count = BinaryPrimitives.ReadInt32LittleEndian(rom.Slice(at, 4));
        if (count < 0 || at + 4 + (long)count * RecordBytes > rom.Length)
            return null;

        var entities = new GbaLevelEntity[count];
        for (var i = 0; i < count; i++)
        {
            var o = at + 4 + i * RecordBytes;
            entities[i] = new GbaLevelEntity(
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(o, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(o + 2, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(o + 4, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(o + 6, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(o + 8, 2)),
                BinaryPrimitives.ReadInt16LittleEndian(rom.Slice(o + 10, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(o + 12, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(o + 14, 2)));
        }

        return entities;
    }

    /// <summary>Where a level's table sits in the ROM, for adjacency checks.</summary>
    public static int TableOffset(ReadOnlySpan<byte> rom, int trueRecordOffset)
    {
        var pointer = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecordOffset + TableField, 4));
        return (int)(pointer - RomBase);
    }

    /// <summary>
    ///     Every level record in the ROM's table, INCLUDING the ones
    ///     <see cref="GbaLevelImages.FindLevels" /> drops.
    /// </summary>
    /// <remarks>
    ///     That method deduplicates by <c>(objectList, elementLibrary)</c> because it
    ///     renders art, and variants of one level share their art. Each variant still
    ///     carries its OWN entity table, so anything counting entities must walk the
    ///     raw table or it silently loses those records.
    /// </remarks>
    public static IReadOnlyList<int> FindLevelRecordOffsets(ReadOnlySpan<byte> rom)
    {
        var levels = GbaLevelImages.FindLevels(rom);
        if (levels.Count == 0) return [];

        // FindLevels always keeps the first record, so its address is the table start.
        var start = (int)(levels[0].RecordAddress - RomBase) - ScanRecordDelta;
        var offsets = new List<int>();
        for (var rec = start; rec + LevelRecordStride <= rom.Length; rec += LevelRecordStride)
        {
            // Same validation FindLevels applies, on the same two fields.
            var scan = rec + ScanRecordDelta;
            if (scan + 12 > rom.Length) break;
            var objectList = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(scan + 4, 4));
            var elementLib = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(scan + 8, 4));
            if (!IsRomPointer(rom, objectList) || !IsRomPointer(rom, elementLib))
                break;
            offsets.Add(rec);
        }

        return offsets;
    }

    private static bool IsRomPointer(ReadOnlySpan<byte> rom, uint value) =>
        value >= RomBase && value - RomBase < (uint)rom.Length;
}
