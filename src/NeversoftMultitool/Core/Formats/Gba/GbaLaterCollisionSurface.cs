using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Collision geometry used by THPS4, THUG, THUG2 and American Sk8land on GBA.
///
///     <para>The parent level record points to a self-contained complex whose 0x30-byte
///     header is <c>{u32 width, height; ptr u16CellGrid; ptr cellRecords;
///     ptr surfaceBank; ...}</c>. The cell grid starts immediately after the header,
///     and the record bank follows its aligned end. THPS4 uses 12-byte cells; the
///     following three games use 20-byte cells. Both store a signed 20.12 base height
///     at +0, a height-object index at +4, a material/classification index at +6,
///     and the shape dispatcher byte at +0x0A/+0x0C.</para>
///
///     <para>A surface object is 40 bytes. Its first word is a THUMB height function;
///     the remaining words are the authored parameters or pointers used by that
///     function. The game transforms the local cell coordinates by the cell's shape,
///     calls that function, and adds the cell base. <see cref="GbaThumbCpu" /> executes
///     the original function, including composite ramps and curves, so this is the
///     collision surface the game queries rather than a flat-height approximation.</para>
/// </summary>
public static class GbaLaterCollisionSurface
{
    private const uint RomBase = 0x08000000;
    private const int HeaderBytes = 0x30;
    private const int HeaderPointerCount = 10;
    private const int SurfaceBytes = 40;
    private const int Thps4CellBytes = 12;
    private const int LaterCellBytes = 20;
    // The latest revision's collision field is +0x44. Stopping there also avoids
    // walking into the next THPS4 parent record (whose stride is only 0x3C).
    private const int ParentScanBytes = 0x44;
    private const int SquareSpan = 0x2FFF;
    private const int HalfCell = 0x1800;
    private const int DiagonalScale = 0x5A82; // sqrt(1/2), signed 1.15

    public enum CellRevision
    {
        Thps4,
        Underground
    }

    public readonly record struct GbaLaterCollisionCell(
        int BaseHeight, ushort HeightSurface, ushort Material, byte Shape, ushort Flags);

    public sealed class Grid : IGbaCollisionGrid
    {
        private readonly int[] _cellRecordIndex;
        private readonly byte[][] _records;
        private readonly byte[][] _surfaces;
        private readonly uint[] _accessors;
        private readonly uint _surfaceBankAddress;
        private readonly uint _runtimeObjectBankAddress;
        private readonly IReadOnlyDictionary<uint, uint>? _runtimeWords;
        private readonly IReadOnlyDictionary<uint, byte>? _runtimeBytes;
        private readonly Dictionary<(int Record, int Samples), int[]> _cache = [];
        private readonly GbaThumbCpu _cpu = new();
        private readonly int _shapeOffset;

        internal Grid(
            int headerOffset,
            int width,
            int height,
            CellRevision revision,
            int[] cellRecordIndex,
            byte[][] records,
            byte[][] surfaces,
            uint[] accessors,
            uint surfaceBankAddress,
            uint runtimeObjectBankAddress,
            IReadOnlyDictionary<uint, uint>? runtimeWords,
            IReadOnlyDictionary<uint, byte>? runtimeBytes)
        {
            HeaderOffset = headerOffset;
            Width = width;
            Height = height;
            Revision = revision;
            _cellRecordIndex = cellRecordIndex;
            _records = records;
            _surfaces = surfaces;
            _accessors = accessors;
            _surfaceBankAddress = surfaceBankAddress;
            _runtimeObjectBankAddress = runtimeObjectBankAddress;
            _runtimeWords = runtimeWords;
            _runtimeBytes = runtimeBytes;
            _shapeOffset = revision == CellRevision.Thps4 ? 0x0A : 0x0C;
        }

        public int HeaderOffset { get; }
        public int Width { get; }
        public int Height { get; }
        public CellRevision Revision { get; }
        public int RecordCount => _records.Length;
        public int SurfaceCount => _surfaces.Length;

        public GbaLaterCollisionCell CellAt(int x, int y)
        {
            ValidateCell(x, y);
            var record = _records[_cellRecordIndex[y * Width + x]];
            return new GbaLaterCollisionCell(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6, 2)),
                record[_shapeOffset],
                Revision == CellRevision.Underground
                    ? BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(0x10, 2))
                    : (ushort)0);
        }

        public int SurfaceAt(int x, int y) => CellAt(x, y).Material;

        /// <summary>The index of the 40-byte height object used by this cell.</summary>
        public int HeightSurfaceAt(int x, int y) => CellAt(x, y).HeightSurface;

        public int[] SampleCell(ReadOnlySpan<byte> rom, int x, int y, int samples)
        {
            ValidateCell(x, y);
            if (samples <= 0 || samples > 65)
                throw new ArgumentOutOfRangeException(nameof(samples));

            var recordIndex = _cellRecordIndex[y * Width + x];
            var key = (recordIndex, samples);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var cell = CellAt(x, y);
            var surface = _surfaces[cell.HeightSurface];
            var accessor = _accessors[cell.HeightSurface];
            var result = new int[samples * samples];
            for (var j = 0; j < samples; j++)
            {
                var v = samples == 1 ? 0 : Math.Min(j * GbaThumbCpu.CellSpan / (samples - 1),
                    GbaThumbCpu.CellSpan - 1);
                for (var i = 0; i < samples; i++)
                {
                    var u = samples == 1 ? 0 : Math.Min(i * GbaThumbCpu.CellSpan / (samples - 1),
                        GbaThumbCpu.CellSpan - 1);
                    var (a, b) = ShapeTransform(cell.Shape, u, v);
                    try
                    {
                        result[j * samples + i] = checked(cell.BaseHeight +
                            _cpu.Run(
                                rom, accessor, a, b, surface,
                                _surfaceBankAddress,
                                _runtimeWords,
                                _runtimeObjectBankAddress,
                                _runtimeBytes));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or OverflowException)
                    {
                        throw new InvalidDataException(
                            $"later collision accessor 0x{accessor:X8} failed for cell ({x}, {y}), " +
                            $"record {recordIndex}, surface {cell.HeightSurface}, shape {cell.Shape}", exception);
                    }
                }
            }

            _cache[key] = result;
            return result;
        }

        public bool IsSloped(ReadOnlySpan<byte> rom, int x, int y)
        {
            var samples = SampleCell(rom, x, y, 3);
            for (var i = 1; i < samples.Length; i++)
                if (samples[i] != samples[0])
                    return true;
            return false;
        }

        private void ValidateCell(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"collision cell ({x}, {y})");
        }
    }

    /// <summary>Loads the collision complex referenced by a discovered later level.</summary>
    public static Grid? TryLoad(ReadOnlySpan<byte> rom, GbaLaterLevelArt.LaterLevel level) =>
        TryLoad(rom, level.LevelRecordOffset);

    /// <summary>
    ///     Finds and loads the collision complex referenced by a later parent record.
    ///     The pointer field moved between game revisions, so it is identified by the
    ///     complex's own closed layout instead of a title/version table.
    /// </summary>
    public static Grid? TryLoad(ReadOnlySpan<byte> rom, int parentRecordOffset)
    {
        if (parentRecordOffset < 0 || parentRecordOffset + 4 > rom.Length)
            return null;

        Grid? found = null;
        var end = Math.Min(rom.Length - 4, parentRecordOffset + ParentScanBytes);
        for (var field = parentRecordOffset; field <= end; field += 4)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(field, 4));
            if (!IsRomPointer(rom, pointer))
                continue;
            var candidate = TryLoadHeader(rom, (int)(pointer - RomBase));
            if (candidate == null)
                continue;
            if (found != null && found.HeaderOffset != candidate.HeaderOffset)
                return null; // fail closed if a parent ever becomes ambiguous
            found = candidate;
        }

        return found;
    }

    /// <summary>The exact coordinate transform used by the later engine's shape dispatcher.</summary>
    public static (int A, int B) ShapeTransform(int shape, int u, int v) => shape switch
    {
        0 or 8 => (u, v),
        1 => (SquareSpan - v, u),
        2 => (SquareSpan - u, SquareSpan - v),
        3 => (v, SquareSpan - u),
        4 => (v, u),
        5 => (u, SquareSpan - v),
        6 => (SquareSpan - v, SquareSpan - u),
        7 => (SquareSpan - u, v),
        9 => (Diagonal(u + v - GbaThumbCpu.CellSpan), Diagonal(v - u)),
        10 => (Diagonal(v - u), Diagonal(GbaThumbCpu.CellSpan - u - v)),
        11 => (Diagonal(GbaThumbCpu.CellSpan - u - v), Diagonal(u - v)),
        12 => (Diagonal(u - v), Diagonal(u + v - GbaThumbCpu.CellSpan)),
        _ => throw new InvalidDataException($"later collision shape {shape} is out of range")
    };

    private static int Diagonal(int delta)
    {
        var product = delta * DiagonalScale;
        if (product < 0)
            product += 0x7FFF; // the ROM's signed round-towards-zero correction
        return (product >> 15) + HalfCell;
    }

    private static Grid? TryLoadHeader(ReadOnlySpan<byte> rom, int header)
    {
        if (header < 0 || header + HeaderBytes > rom.Length)
            return null;
        var widthValue = ReadU32(rom, header);
        var heightValue = ReadU32(rom, header + 4);
        if (widthValue is < 4 or > 128 || heightValue is < 4 or > 128)
            return null;
        var width = (int)widthValue;
        var height = (int)heightValue;

        for (var i = 0; i < HeaderPointerCount; i++)
            if (!IsRomPointer(rom, ReadU32(rom, header + 8 + i * 4)))
                return null;

        var gridOffset = (int)(ReadU32(rom, header + 8) - RomBase);
        var recordsOffset = (int)(ReadU32(rom, header + 0x0C) - RomBase);
        var surfacesOffset = (int)(ReadU32(rom, header + 0x10) - RomBase);
        var cellCount = checked(width * height);
        if (gridOffset != header + HeaderBytes || gridOffset + cellCount * 2 > rom.Length)
            return null;
        if (recordsOffset != Align4(gridOffset + cellCount * 2) || recordsOffset >= surfacesOffset)
            return null;

        var cellRecords = new int[cellCount];
        var maxRecord = 0;
        for (var i = 0; i < cellCount; i++)
        {
            var record = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(gridOffset + i * 2, 2));
            cellRecords[i] = record;
            maxRecord = Math.Max(maxRecord, record);
        }

        // Every shipped complex numbers its records densely. Requiring that closure
        // prevents unrelated pointer-rich structures from validating accidentally.
        var seen = new bool[maxRecord + 1];
        foreach (var record in cellRecords)
            seen[record] = true;
        if (seen.Any(value => !value))
            return null;

        var revision = ValidateLaterRecords(rom, recordsOffset, surfacesOffset, maxRecord)
            ? CellRevision.Underground
            : ValidateThps4Records(rom, recordsOffset, surfacesOffset, maxRecord)
                ? CellRevision.Thps4
                : (CellRevision?)null;
        if (revision == null)
            return null;

        var recordBytes = revision == CellRevision.Thps4 ? Thps4CellBytes : LaterCellBytes;
        var records = new byte[maxRecord + 1][];
        var maxSurface = 0;
        for (var i = 0; i < records.Length; i++)
        {
            records[i] = rom.Slice(recordsOffset + i * recordBytes, recordBytes).ToArray();
            maxSurface = Math.Max(maxSurface,
                BinaryPrimitives.ReadUInt16LittleEndian(records[i].AsSpan(4, 2)));
        }

        if (surfacesOffset + (long)(maxSurface + 1) * SurfaceBytes > rom.Length)
            return null;
        var surfaces = new byte[maxSurface + 1][];
        var accessors = new uint[maxSurface + 1];
        for (var i = 0; i < surfaces.Length; i++)
        {
            var offset = surfacesOffset + i * SurfaceBytes;
            surfaces[i] = rom.Slice(offset, SurfaceBytes).ToArray();
            for (var slot = 0; slot < 3; slot++)
            {
                var function = BinaryPrimitives.ReadUInt32LittleEndian(surfaces[i].AsSpan(slot * 4, 4));
                if ((function & 1) == 0 || !IsRomPointer(rom, function & ~1u))
                    return null;
            }
            accessors[i] = BinaryPrimitives.ReadUInt32LittleEndian(surfaces[i]) & ~1u;
        }

        var runtime = TryGetRuntimeState(rom);
        if (runtime == null)
            return null;
        return new Grid(
            header, width, height, revision.Value, cellRecords, records, surfaces, accessors,
            RomBase + (uint)surfacesOffset,
            runtime.ObjectBankAddress,
            runtime.Words,
            runtime.Bytes);
    }

    private sealed record RuntimeState(
        uint ObjectBankAddress,
        IReadOnlyDictionary<uint, uint>? Words = null,
        IReadOnlyDictionary<uint, byte>? Bytes = null);

    /// <summary>
    ///     The exact loader-published EWRAM dependencies observed in each engine
    ///     revision. These are deliberately title-code keyed: an unknown revision
    ///     must not inherit a plausible-looking RAM snapshot from another game.
    /// </summary>
    private static RuntimeState? TryGetRuntimeState(ReadOnlySpan<byte> rom)
    {
        if (HasGameCodePrefix(rom, "AT6"u8))
            return new RuntimeState(
                0x02000910,
                Bytes: new Dictionary<uint, byte>
                {
                    // Static/offline state bytes read by two THPS4 objects. Only
                    // these two byte addresses are modelled as zero.
                    [0x0200085C] = 0,
                    [0x0200085E] = 0
                });
        if (HasGameCodePrefix(rom, "BTO"u8))
            return new RuntimeState(0x0200D0D0);
        if (HasGameCodePrefix(rom, "B2T"u8))
            return TryGetPinnedRuntimeFunctionWord(
                    rom, 0x02007BE4, 0x0805003D, out var thug2Words)
                ? new RuntimeState(0, thug2Words)
                : null;
        if (HasGameCodePrefix(rom, "BH9"u8))
            return TryGetPinnedRuntimeFunctionWord(
                    rom, 0x0200820C, 0x0804E3FD, out var sk8landWords)
                ? new RuntimeState(0, sk8landWords)
                : null;
        return null;
    }

    private static bool TryGetPinnedRuntimeFunctionWord(
        ReadOnlySpan<byte> rom,
        uint destination,
        uint expectedFunction,
        out IReadOnlyDictionary<uint, uint> words)
    {
        for (var offset = 0; offset + 8 <= rom.Length; offset += 4)
        {
            if (ReadU32(rom, offset) == destination
                && ReadU32(rom, offset + 4) == expectedFunction)
            {
                words = new Dictionary<uint, uint> { [destination] = expectedFunction };
                return true;
            }
        }

        words = null!;
        return false;
    }

    private static bool HasGameCodePrefix(ReadOnlySpan<byte> rom, ReadOnlySpan<byte> prefix) =>
        rom.Length >= 0xAF && rom.Slice(0xAC, 3).SequenceEqual(prefix);

    private static bool ValidateLaterRecords(
        ReadOnlySpan<byte> rom, int records, int surfaces, int maxRecord)
    {
        if (records + (long)(maxRecord + 1) * LaterCellBytes > surfaces)
            return false;
        for (var i = 0; i <= maxRecord; i++)
        {
            var record = rom.Slice(records + i * LaterCellBytes, LaterCellBytes);
            if (BinaryPrimitives.ReadUInt16LittleEndian(record[10..12]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(record[12..14]) > 12
                || BinaryPrimitives.ReadUInt16LittleEndian(record[14..16]) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(record[18..20]) != 0)
                return false;
        }
        return true;
    }

    private static bool ValidateThps4Records(
        ReadOnlySpan<byte> rom, int records, int surfaces, int maxRecord)
    {
        if (records + (long)(maxRecord + 1) * Thps4CellBytes > surfaces)
            return false;
        for (var i = 0; i <= maxRecord; i++)
            // The byte following the shape carries per-cell flags; it is not part
            // of the dispatcher index (several THPS4 levels set it to 0x04).
            if (rom[records + i * Thps4CellBytes + 10] > 12)
                return false;
        return true;
    }

    private static int Align4(int value) => (value + 3) & ~3;

    private static uint ReadU32(ReadOnlySpan<byte> rom, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));

    private static bool IsRomPointer(ReadOnlySpan<byte> rom, uint address) =>
        address >= RomBase && address < RomBase + (uint)rom.Length;
}
