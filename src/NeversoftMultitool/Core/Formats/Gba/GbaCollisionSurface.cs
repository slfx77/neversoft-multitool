using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The THPS2 GBA collision surface — the level's real 3D geometry, as the engine
///     itself computes it.
///
///     <para>A level carries a coarse grid of cells (record <c>+0x13C/+0x13E</c> for the
///     dimensions, <c>+0x140</c> for a per-cell index, <c>+0x144</c> for a table of
///     32-byte cell records). Each record holds a <b>shape</b> byte at <c>[0]</c>, a
///     <b>material</b> index at <c>[2]</c>, and a base height at <c>[8]</c>. Crucially the
///     surface within a cell is <b>not</b> flat: the engine's height query
///     (ROM 0x08023168) reorients the sub-cell offset by the shape, then calls the
///     material's height function:</para>
///     <code>
///     gx = worldX / CellSpan ; gy = worldY / CellSpan
///     rec = cellRecords[cellIndex[gy*W + gx]]
///     (a, b) = ShapeTransform(rec[0], worldX - gx*CellSpan, worldY - gy*CellSpan)
///     h = materialVtable[rec[2]].slot0(a, b, rec)
///     </code>
///
///     <para>The shape byte selects one of the <b>8 symmetries of the square</b> (the D4
///     group) applied to the sub-cell offset with span constant <c>0x2FFF</c> — transcribed
///     from the jump table at 0x080231D4. The material height functions are <i>executed</i>
///     out of the ROM by <see cref="GbaThumbCpu" /> rather than reimplemented, so ramps,
///     quarter-pipe transitions, bowls and rails come out exactly as the engine computes
///     them. Roughly three quarters of cells are flat; the remaining quarter are the ramps
///     and transitions that a flat-per-cell reading turns into walls and staircases.</para>
/// </summary>
public static class GbaCollisionSurface
{
    /// <summary>World span of one cell in 20.12 fixed point (3 world units).</summary>
    public const int CellSpan = GbaThumbCpu.CellSpan;

    private const uint RomBase = 0x08000000;
    private const uint MaterialVtable = 0x08745028;
    private const int MaterialStride = 20;
    private const int MaterialCount = 37;
    private const int RecordBytes = 32;
    private const int ShapeSpan = 0x2FFF; // the engine's sub-cell span constant

    // Field offsets from the loader's true record base.
    private const int DimsField = 0x13C;
    private const int CellIndexField = 0x140;
    private const int RecordTableField = 0x144;

    /// <summary>One cell's classification, before any height sampling.</summary>
    public readonly record struct GbaCollisionCell(byte Shape, byte Material, int BaseHeight);

    /// <summary>
    ///     A level's loaded collision grid. Sampling is cached per distinct cell record,
    ///     because a level of a few thousand cells typically has only a few hundred
    ///     distinct records.
    /// </summary>
    public sealed class Grid : IGbaCollisionGrid
    {
        private readonly byte[][] _records;      // indexed by cell-record index
        private readonly int[] _cellRecordIndex; // per cell -> record index
        private readonly Dictionary<(int Record, int Samples), int[]> _cache = [];
        private readonly GbaThumbCpu _cpu = new();

        internal Grid(int width, int height, int[] cellRecordIndex, byte[][] records)
        {
            Width = width;
            Height = height;
            _cellRecordIndex = cellRecordIndex;
            _records = records;
        }

        public int Width { get; }
        public int Height { get; }

        public int SurfaceAt(int x, int y) => CellAt(x, y).Material;

        /// <summary>The cell at grid position (x, y).</summary>
        public GbaCollisionCell CellAt(int x, int y)
        {
            var record = _records[_cellRecordIndex[y * Width + x]];
            // The base height at +8 is a full signed 32-bit 20.12 value — the
            // out-of-bounds kill walls exceed 16 bits (34.375 units = 0x22600).
            return new GbaCollisionCell(record[0], record[2],
                BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8, 4)));
        }

        /// <summary>
        ///     Heights across one cell as an <paramref name="samples" />×<paramref name="samples" />
        ///     grid in 20.12 fixed point, row-major with the u axis fastest. These are the
        ///     engine's own values — sloped cells really slope.
        /// </summary>
        public int[] SampleCell(ReadOnlySpan<byte> rom, int x, int y, int samples)
        {
            var recordIndex = _cellRecordIndex[y * Width + x];
            var key = (recordIndex, samples);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var record = _records[recordIndex];
            var accessor = ReadAccessor(rom, record[2]);
            var result = new int[samples * samples];
            for (var j = 0; j < samples; j++)
            {
                var v = samples == 1 ? 0 : Math.Min(j * CellSpan / (samples - 1), CellSpan - 1);
                for (var i = 0; i < samples; i++)
                {
                    var u = samples == 1 ? 0 : Math.Min(i * CellSpan / (samples - 1), CellSpan - 1);
                    var (a, b) = ShapeTransform(record[0], u, v);
                    result[j * samples + i] = _cpu.Run(rom, accessor, a, b, record);
                }
            }

            _cache[key] = result;
            return result;
        }

        /// <summary>True when the cell's surface is not a single flat plane.</summary>
        public bool IsSloped(ReadOnlySpan<byte> rom, int x, int y)
        {
            var s = SampleCell(rom, x, y, 3);
            for (var i = 1; i < s.Length; i++)
                if (s[i] != s[0])
                    return true;
            return false;
        }
    }

    /// <summary>Loads a level's collision grid, or null when the fields do not validate.</summary>
    public static Grid? TryLoad(ReadOnlySpan<byte> rom, int trueRecordOffset)
    {
        if (trueRecordOffset < 0 || trueRecordOffset + RecordTableField + 4 > rom.Length)
            return null;

        int width = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(trueRecordOffset + DimsField, 2));
        int height = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(trueRecordOffset + DimsField + 2, 2));
        var cellIndexPtr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecordOffset + CellIndexField, 4));
        var recordTablePtr = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(trueRecordOffset + RecordTableField, 4));
        if (width is <= 0 or > 256 || height is <= 0 or > 256)
            return null;
        if (!IsRomPointer(rom, cellIndexPtr) || !IsRomPointer(rom, recordTablePtr))
            return null;

        var cellIndexOffset = (int)(cellIndexPtr - RomBase);
        var recordTableOffset = (int)(recordTablePtr - RomBase);
        if (cellIndexOffset + width * height * 2 > rom.Length)
            return null;

        var cells = new int[width * height];
        var maxRecord = 0;
        for (var i = 0; i < cells.Length; i++)
        {
            cells[i] = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(cellIndexOffset + i * 2, 2));
            maxRecord = Math.Max(maxRecord, cells[i]);
        }

        if (recordTableOffset + (maxRecord + 1) * RecordBytes > rom.Length)
            return null;

        var records = new byte[maxRecord + 1][];
        for (var i = 0; i <= maxRecord; i++)
            records[i] = rom.Slice(recordTableOffset + i * RecordBytes, RecordBytes).ToArray();

        return new Grid(width, height, cells, records);
    }

    /// <summary>
    ///     The D4 sub-cell reorientation the shape byte selects (jump table 0x080231D4).
    ///     Shapes 0-3 are rotations, 4-7 the reflected set.
    /// </summary>
    public static (int A, int B) ShapeTransform(int shape, int u, int v) => (shape & 7) switch
    {
        0 => (u, v),
        1 => (ShapeSpan - v, u),
        2 => (ShapeSpan - u, ShapeSpan - v),
        3 => (v, ShapeSpan - u),
        4 => (v, u),
        5 => (u, ShapeSpan - v),
        6 => (ShapeSpan - v, ShapeSpan - u),
        _ => (ShapeSpan - u, v)
    };

    // Slot 0 of the material's vtable is its height accessor; the low bit is the THUMB flag.
    private static uint ReadAccessor(ReadOnlySpan<byte> rom, int material)
    {
        if (material >= MaterialCount)
            throw new InvalidDataException($"collision material {material} is out of range");
        var offset = (int)(MaterialVtable - RomBase) + material * MaterialStride;
        return BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4)) & ~1u;
    }

    private static bool IsRomPointer(ReadOnlySpan<byte> rom, uint address) =>
        address >= RomBase && address < RomBase + (uint)rom.Length;
}
