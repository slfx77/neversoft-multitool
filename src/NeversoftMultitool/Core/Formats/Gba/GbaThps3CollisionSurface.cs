using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Tony Hawk's Pro Skater 3 GBA collision geometry.
///
///     <para>The 0x70-byte THPS3 level record stores the collision dimensions at
///     <c>+0x2C</c>, followed by pointers to a u16 cell-index grid at <c>+0x34</c>,
///     12-byte cell records at <c>+0x38</c>, and 40-byte height objects at
///     <c>+0x3C</c>. The cartridge's query at <c>0x08008C2C</c> establishes a
///     0x3000-fixed-point cell span, row-major indexing, and those exact fields.</para>
///
///     <para>A cell is <c>{s32 baseHeight, u16 heightObject, u16 material,
///     u16 detail, u8 shape, u8 flags}</c>. The height query at
///     <c>0x08008C98</c> transforms the local coordinates through the shape
///     dispatcher, invokes slot zero of the selected height object, then adds the
///     cell's base. <see cref="GbaThumbCpu" /> executes those cartridge routines,
///     including composite ramps which recursively query other authored cells.</para>
/// </summary>
public static class GbaThps3CollisionSurface
{
    private const uint RomBase = 0x08000000;
    private const int CellBytes = 12;
    private const int HeightObjectBytes = 40;
    private const int SquareSpan = 0x2FFF;
    private const int HalfCell = 0x1800;
    private const int DiagonalScale = 0x5A82; // sqrt(1/2), signed 1.15
    private const uint DynamicGeometryAccessor = 0x08006F18;
    private const uint ObjectPresenceAccessor = 0x08006DF8;
    private const uint ObjectBytePresenceAccessor = 0x08005EE8;
    private const uint PlayerRelativeAccessor = 0x08005B98;
    private const uint PlayerRelativeWrapperAccessor = 0x08006E84;
    private const uint CompositeAccessor = 0x0800587C;

    // 0x08005B98 reads global -> +4 -> [0] -> +0x10. The scalar is zero in
    // THPS3's unloaded/default player state (and in the retained title/menu
    // captures). Publish that deterministic state through harmless synthetic
    // EWRAM nodes. Gameplay can animate it, so affected cells remain labelled
    // runtime-dependent through the public API.
    private static readonly IReadOnlyDictionary<uint, uint> OfflineRuntimeWords =
        new Dictionary<uint, uint>
        {
            // Live level-object manager consumed only by the two presence hooks.
            [0x0200008C] = 0,
            [0x020000E4] = 0x0203FF00,
            [0x0203FF04] = 0x0203FF10,
            [0x0203FF10] = 0x0203FF20,
            [0x0203FF30] = 0
        };

    // Offsets in THPS3's 0x70-byte parent level record.
    private const int DimensionsField = 0x2C;
    private const int CellGridField = 0x34;
    private const int CellRecordsField = 0x38;
    private const int HeightObjectsField = 0x3C;

    public readonly record struct GbaThps3CollisionCell(
        int BaseHeight,
        ushort HeightObject,
        ushort Material,
        ushort Detail,
        byte Shape,
        byte Flags);

    public sealed class Grid : IGbaCollisionGrid
    {
        private readonly int[] _cellRecordIndices;
        private readonly byte[][] _records;
        private readonly byte[][] _heightObjects;
        private readonly uint[] _heightAccessors;
        private readonly bool[] _runtimeDependentHeightObjects;
        private readonly uint _parentRecordAddress;
        private readonly Dictionary<(int Record, int Samples), int[]> _cache = [];
        private readonly GbaThumbCpu _cpu = new();

        internal Grid(
            int levelRecordOffset,
            int width,
            int height,
            int[] cellRecordIndices,
            byte[][] records,
            byte[][] heightObjects,
            uint[] heightAccessors,
            bool[] runtimeDependentHeightObjects)
        {
            LevelRecordOffset = levelRecordOffset;
            Width = width;
            Height = height;
            _cellRecordIndices = cellRecordIndices;
            _records = records;
            _heightObjects = heightObjects;
            _heightAccessors = heightAccessors;
            _runtimeDependentHeightObjects = runtimeDependentHeightObjects;
            _parentRecordAddress = RomBase + (uint)levelRecordOffset;
        }

        public int LevelRecordOffset { get; }
        public int Width { get; }
        public int Height { get; }
        public int RecordCount => _records.Length;
        public int HeightObjectCount => _heightObjects.Length;
        public int ReferencedRecordCount => _cellRecordIndices.Distinct().Count();
        public int RuntimeDependentHeightObjectCount =>
            _runtimeDependentHeightObjects.Count(value => value);
        public int RuntimeDependentCellCount => _cellRecordIndices.Count(index =>
            _runtimeDependentHeightObjects[CellFromRecord(index).HeightObject]);

        public GbaThps3CollisionCell CellAt(int x, int y)
        {
            ValidateCell(x, y);
            return CellFromRecord(_cellRecordIndices[y * Width + x]);
        }

        /// <summary>The gameplay-material value stored independently of the height object.</summary>
        public int SurfaceAt(int x, int y) => CellAt(x, y).Material;

        /// <summary>The 40-byte height object which computes this cell's local surface.</summary>
        public int HeightObjectAt(int x, int y) => CellAt(x, y).HeightObject;

        /// <summary>
        ///     True for cells whose optional contribution comes from a live level
        ///     object or the dynamic-geometry manager. Offline sampling uses the
        ///     cartridge's empty-scene result (zero local contribution), while
        ///     retaining the cell's authored base height.
        /// </summary>
        public bool IsRuntimeDependent(int x, int y)
        {
            var cell = CellAt(x, y);
            return _runtimeDependentHeightObjects[cell.HeightObject];
        }

        public int[] SampleCell(ReadOnlySpan<byte> rom, int x, int y, int samples)
        {
            ValidateCell(x, y);
            if (samples <= 0 || samples > 65)
                throw new ArgumentOutOfRangeException(nameof(samples));

            var recordIndex = _cellRecordIndices[y * Width + x];
            var key = (recordIndex, samples);
            if (_cache.TryGetValue(key, out var cached))
                return cached;

            var cell = CellAt(x, y);
            var heightObject = _heightObjects[cell.HeightObject];
            var accessor = _heightAccessors[cell.HeightObject];
            var result = new int[samples * samples];
            for (var j = 0; j < samples; j++)
            {
                var v = SampleCoordinate(j, samples);
                for (var i = 0; i < samples; i++)
                {
                    var u = SampleCoordinate(i, samples);
                    var (a, b) = ShapeTransform(cell.Shape, u, v);
                    try
                    {
                        // Composite THPS3 objects call the cartridge's generic
                        // height query, which dereferences the live parent record.
                        // Publish the ROM parent through the interpreter's modelled
                        // runtime pointer so the recursive call sees +0x3C exactly
                        // as it does in the game.
                        result[j * samples + i] = checked(cell.BaseHeight
                            + _cpu.Run(rom, accessor, a, b, heightObject,
                                _parentRecordAddress, OfflineRuntimeWords,
                                runtimeObjectBankAddress: 0x02000494));
                    }
                    catch (Exception exception) when (exception is InvalidDataException or OverflowException)
                    {
                        throw new InvalidDataException(
                            $"THPS3 collision accessor 0x{accessor:X8} failed for cell ({x}, {y}), "
                            + $"record {recordIndex}, height object {cell.HeightObject}, shape {cell.Shape}",
                            exception);
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

        private static int SampleCoordinate(int index, int samples) =>
            samples == 1
                ? 0
                : Math.Min(index * GbaThumbCpu.CellSpan / (samples - 1), GbaThumbCpu.CellSpan - 1);

        private void ValidateCell(int x, int y)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height)
                throw new ArgumentOutOfRangeException($"collision cell ({x}, {y})");
        }

        private GbaThps3CollisionCell CellFromRecord(int recordIndex)
        {
            var record = _records[recordIndex];
            return new GbaThps3CollisionCell(
                BinaryPrimitives.ReadInt32LittleEndian(record),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(6, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(8, 2)),
                record[10],
                record[11]);
        }
    }

    /// <summary>Loads the collision complex belonging to a discovered THPS3 level.</summary>
    public static Grid? TryLoad(ReadOnlySpan<byte> rom, GbaThps3LevelArt.Thps3Level level) =>
        TryLoad(rom, level.LevelRecordOffset);

    /// <summary>
    ///     Structurally closes a THPS3 collision complex from its parent level record.
    ///     No fixed regional ROM address is required.
    /// </summary>
    public static Grid? TryLoad(ReadOnlySpan<byte> rom, int levelRecordOffset)
    {
        if (levelRecordOffset < 0
            || levelRecordOffset + GbaThps3LevelArt.LevelRecordStride > rom.Length)
            return null;

        var width = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(levelRecordOffset + DimensionsField, 2));
        var height = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(levelRecordOffset + DimensionsField + 2, 2));
        if (width is < 4 or > 128 || height is < 4 or > 128)
            return null;

        var cellGridOffset = PointerToOffset(rom, levelRecordOffset + CellGridField);
        var recordsOffset = PointerToOffset(rom, levelRecordOffset + CellRecordsField);
        var heightObjectsOffset = PointerToOffset(rom, levelRecordOffset + HeightObjectsField);
        if (cellGridOffset < 0 || recordsOffset < 0 || heightObjectsOffset < 0)
            return null;

        var cellCount = checked(width * height);
        var cellGridEnd = cellGridOffset + (long)cellCount * 2;
        if (cellGridEnd > recordsOffset || recordsOffset - cellGridEnd > 2
            || (heightObjectsOffset - recordsOffset) % CellBytes != 0)
            return null;
        // The sole alignment halfword in odd-sized grids is zero in the corpus.
        for (var offset = (int)cellGridEnd; offset < recordsOffset; offset++)
            if (rom[offset] != 0)
                return null;

        var recordCount = (heightObjectsOffset - recordsOffset) / CellBytes;
        if (recordCount <= 0)
            return null;
        var cellRecordIndices = new int[cellCount];
        for (var i = 0; i < cellCount; i++)
        {
            var record = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(cellGridOffset + i * 2, 2));
            if (record >= recordCount)
                return null;
            cellRecordIndices[i] = record;
        }

        var records = new byte[recordCount][];
        var maxHeightObject = 0;
        for (var i = 0; i < recordCount; i++)
        {
            var record = rom.Slice(recordsOffset + i * CellBytes, CellBytes);
            if (record[10] > 12)
                return null;
            records[i] = record.ToArray();
            maxHeightObject = Math.Max(maxHeightObject,
                BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(4, 2)));
        }

        var heightObjectCount = maxHeightObject + 1;
        if (heightObjectsOffset + (long)heightObjectCount * HeightObjectBytes > rom.Length)
            return null;
        var heightObjects = new byte[heightObjectCount][];
        var accessors = new uint[heightObjectCount];
        for (var i = 0; i < heightObjectCount; i++)
        {
            var heightObject = rom.Slice(heightObjectsOffset + i * HeightObjectBytes, HeightObjectBytes);
            // The first three slots are height/normal/auxiliary functions. All
            // authored THPS3 objects carry valid THUMB pointers in all three.
            for (var slot = 0; slot < 3; slot++)
            {
                var function = BinaryPrimitives.ReadUInt32LittleEndian(heightObject.Slice(slot * 4, 4));
                if ((function & 1) == 0 || !IsRomPointer(rom, function & ~1u))
                    return null;
            }
            heightObjects[i] = heightObject.ToArray();
            accessors[i] = BinaryPrimitives.ReadUInt32LittleEndian(heightObject) & ~1u;
        }

        // Every height-object id through the maximum is referenced by at least one
        // record. This tight closure is a useful false-positive guard.
        var seenHeightObjects = new bool[heightObjectCount];
        foreach (var record in records)
            seenHeightObjects[BinaryPrimitives.ReadUInt16LittleEndian(record.AsSpan(4, 2))] = true;
        if (seenHeightObjects.Any(seen => !seen))
            return null;

        // Composite objects (0x0800587C) dispatch through two ordinary 12-byte
        // records. Close those references and propagate runtime dependence so
        // callers can distinguish a fully-authored surface from a cell whose
        // result also depends on live objects/player state.
        var compositeChildren = new (int First, int Second)?[heightObjectCount];
        for (var i = 0; i < heightObjectCount; i++)
        {
            if (accessors[i] != CompositeAccessor)
                continue;
            var first = ReferencedRecordIndex(heightObjects[i], 0x14, recordsOffset, recordCount);
            var second = ReferencedRecordIndex(heightObjects[i], 0x18, recordsOffset, recordCount);
            if (first < 0 || second < 0)
                return null;
            compositeChildren[i] = (
                BinaryPrimitives.ReadUInt16LittleEndian(records[first].AsSpan(4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(records[second].AsSpan(4, 2)));
        }

        var runtimeDependent = new bool[heightObjectCount];
        var visit = new byte[heightObjectCount];
        var cyclic = false;
        bool DependsOnRuntime(int index)
        {
            if (visit[index] == 2)
                return runtimeDependent[index];
            if (visit[index] == 1)
            {
                cyclic = true;
                return false;
            }
            visit[index] = 1;
            var result = IsRuntimeDependentAccessor(accessors[index]);
            if (compositeChildren[index] is { } children)
                result |= DependsOnRuntime(children.First) || DependsOnRuntime(children.Second);
            runtimeDependent[index] = result;
            visit[index] = 2;
            return result;
        }
        for (var i = 0; i < heightObjectCount; i++)
            _ = DependsOnRuntime(i);
        if (cyclic)
            return null;

        return new Grid(levelRecordOffset, width, height, cellRecordIndices,
            records, heightObjects, accessors, runtimeDependent);
    }

    /// <summary>The exact 13-case transform at THPS3 ROM 0x080074A0.</summary>
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
        _ => throw new InvalidDataException($"THPS3 collision shape {shape} is out of range")
    };

    private static int Diagonal(int delta)
    {
        var product = delta * DiagonalScale;
        if (product < 0)
            product += 0x7FFF; // cartridge uses signed round-towards-zero correction
        return (product >> 15) + HalfCell;
    }

    private static int PointerToOffset(ReadOnlySpan<byte> rom, int pointerSite)
    {
        if (pointerSite < 0 || pointerSite + 4 > rom.Length)
            return -1;
        var address = BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(pointerSite, 4));
        return IsRomPointer(rom, address) ? (int)(address - RomBase) : -1;
    }

    private static int ReferencedRecordIndex(
        ReadOnlySpan<byte> heightObject,
        int field,
        int recordsOffset,
        int recordCount)
    {
        var address = BinaryPrimitives.ReadUInt32LittleEndian(heightObject.Slice(field, 4));
        if (address < RomBase)
            return -1;
        var offset = (long)address - RomBase;
        var delta = offset - recordsOffset;
        if (delta < 0 || delta % CellBytes != 0)
            return -1;
        var index = delta / CellBytes;
        return index < recordCount ? (int)index : -1;
    }

    private static bool IsRomPointer(ReadOnlySpan<byte> rom, uint address) =>
        address >= RomBase && address < RomBase + (uint)rom.Length;

    private static bool IsRuntimeDependentAccessor(uint accessor) =>
        accessor is DynamicGeometryAccessor
            or ObjectPresenceAccessor
            or ObjectBytePresenceAccessor
            or PlayerRelativeAccessor
            or PlayerRelativeWrapperAccessor;
}
