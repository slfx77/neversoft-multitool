using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>One piece of a model set, as the game's own code declares it.</summary>
/// <param name="IdB">The geometry file's second id.</param>
/// <param name="TextureBankId">
///     The set whose <c>textureinfo</c> bank this piece draws with, or 0 for none.
///     Equal to the owning idA wherever it is non-zero (0 of 3,032 records name a
///     foreign set), so what it adds over the shipped spelling-based binding is the
///     ZERO case: 529 records across the three carts declare no bank at all, which
///     the GX-state join cannot say and can only guess at.
/// </param>
/// <param name="AnimationId">
///     The set whose <c>.\&lt;id&gt;.animation.bin</c> clip drives this piece, or 0.
///     Downhill Jam and Proving Ground name their clips this way; Sk8land instead
///     spells indexed clips per piece and leaves this word as a runtime cache slot,
///     null on disc — so the field is only reported when the file it names exists.
/// </param>
/// <param name="VisibilityMask">
///     The 64-bit potentially-visible set: bit <i>i</i> means "drawn while the camera
///     is in sector <i>i</i>", the sectors being the boxes in the set's <c>pvs</c>
///     file. All ones where a set ships no <c>pvs</c>.
/// </param>
/// <param name="ClassFlags">The record's class word; see <see cref="IsCamera" />.</param>
/// <param name="Name">The exporter's own object name, when the overlay carries it.</param>
public sealed record NdsManifestPiece(
    uint IdB,
    uint TextureBankId,
    uint AnimationId,
    ulong VisibilityMask,
    uint ClassFlags,
    string? Name)
{
    /// <summary>
    ///     Class 3 marks a piece that is never drawn. Every such record declares no
    ///     texture bank (529/529 and no others), its geometry issues no POLYGON_ATTR
    ///     at all, and where the overlay carries names they are all <c>Camera_*</c> /
    ///     <c>Cam_*</c> / <c>menu_*</c> — the same population
    ///     <see cref="NdsGeometryFile.IsCameraRig" /> finds from the file side.
    /// </summary>
    public bool IsCamera => ClassFlags == 3;
}

/// <summary>
///     One model set as the cart's code declares it: which geometry pieces belong to
///     it, what each draws with, and what each is called.
///
///     The container has no such table — a model set is only "the files sharing an
///     id" there. The declaration lives in ARM9 and the ARM9 overlays, one table per
///     set, as a run of fixed-stride records opening with the set's own
///     <c>(idA, idB)</c> pair. That is the same place the ids themselves were
///     recovered from, and the two agree exactly: every table's membership is
///     precisely the set of geometry files sharing its idA, with no extras and none
///     missing, across all 47 tables in the three carts.
///
///     Nothing here is per-cart constant. The record STRIDE differs between the three
///     builds (32 / 36 / 28 bytes) and is measured per table; the field positions are
///     read relative to both ends of the record, which is what makes one reading cover
///     all three; and a table is only accepted when its membership reproduces the
///     container's own grouping, so a run of code words that merely looks like records
///     is rejected rather than reported.
/// </summary>
public sealed class NdsModelSetManifest
{
    /// <summary>Strides seen across the shipped carts, smallest first.</summary>
    private static readonly int[] CandidateStrides = [28, 32, 36];

    /// <summary>A run shorter than this is not worth testing against the container.</summary>
    private const int MinimumRecords = 4;

    private NdsModelSetManifest(uint idA, string region, int offset, int stride,
        IReadOnlyList<NdsManifestPiece> pieces)
    {
        IdA = idA;
        Region = region;
        Offset = offset;
        Stride = stride;
        Pieces = pieces;
    }

    /// <summary>The model set this table declares.</summary>
    public uint IdA { get; }

    /// <summary>Which code region holds it — <c>arm9</c> or an overlay's name.</summary>
    public string Region { get; }

    /// <summary>Byte offset of the first record within that region.</summary>
    public int Offset { get; }

    /// <summary>Record stride in bytes, measured from the run itself.</summary>
    public int Stride { get; }

    public IReadOnlyList<NdsManifestPiece> Pieces { get; }

    /// <summary>True when every piece names its texture bank.</summary>
    public bool DeclaresTextureBanks => Pieces.All(p => p.TextureBankId != 0);

    /// <summary>
    ///     Finds every manifest table across the supplied code regions.
    /// </summary>
    /// <param name="regions">
    ///     Code images to scan — ARM9 and every ARM9 overlay. The virtual base is only
    ///     used to follow the name pointers, and a wrong one costs names, never
    ///     records; pass 0 to have it derived from the pointers themselves.
    /// </param>
    /// <param name="geometry">
    ///     The container's own grouping — idA to the idBs of the geometry files
    ///     sharing it. This is the gate: a candidate run is a manifest only if it
    ///     reproduces one of these groups exactly.
    /// </param>
    public static IReadOnlyList<NdsModelSetManifest> Locate(
        IReadOnlyList<(string Name, uint VirtualBase, byte[] Data)> regions,
        IReadOnlyDictionary<uint, IReadOnlyCollection<uint>> geometry)
    {
        var found = new List<NdsModelSetManifest>();
        foreach (var (name, virtualBase, data) in regions)
        {
            foreach (var stride in CandidateStrides)
            {
                var at = 0;
                while (at + stride * MinimumRecords <= data.Length)
                {
                    var run = MeasureRun(data, at, stride, geometry);
                    if (run == 0)
                    {
                        at += 4;
                        continue;
                    }

                    var idA = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    var names = ReadNames(data, virtualBase, at, stride, run);
                    found.Add(new NdsModelSetManifest(idA, name, at, stride,
                        ReadPieces(data, at, stride, run, names)));
                    at += run * stride;
                }
            }
        }

        // A table found at one stride cannot also be a table at another; keep the
        // first reading of any given set so a coincidental longer run cannot displace
        // the one that matched the container.
        return found
            .GroupBy(m => m.IdA)
            .Select(g => g.OrderByDescending(m => m.Pieces.Count).First())
            .ToList();
    }

    /// <summary>
    ///     Length of the record run starting at <paramref name="at" />, or 0 when it is
    ///     not a manifest. The run must share one idA, its idBs must be distinct, every
    ///     pair must be a real geometry file, and — the gate that makes this a
    ///     recognition rather than a pattern match — the run must cover that idA's
    ///     whole group with nothing left over.
    /// </summary>
    private static int MeasureRun(
        byte[] data, int at, int stride,
        IReadOnlyDictionary<uint, IReadOnlyCollection<uint>> geometry)
    {
        var idA = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        if (!geometry.TryGetValue(idA, out var group) || group.Count < MinimumRecords)
            return 0;

        var seen = new HashSet<uint>();
        var members = group as IReadOnlySet<uint> ?? group.ToHashSet();
        var count = 0;
        while (at + count * stride + stride <= data.Length)
        {
            var offset = at + count * stride;
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) != idA)
                break;
            var idB = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (!members.Contains(idB) || !seen.Add(idB))
                break;
            count++;
        }

        return count == members.Count ? count : 0;
    }

    private static IReadOnlyList<NdsManifestPiece> ReadPieces(
        byte[] data, int at, int stride, int count, IReadOnlyDictionary<int, string> names)
    {
        var words = stride / 4;
        var pieces = new List<NdsManifestPiece>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = at + i * stride;
            uint Word(int index) =>
                BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + index * 4));

            // Read from both ends: the head is fixed across the three builds and the
            // class word is always last, with the mask in the two words before it.
            // Downhill Jam's wider record simply leaves those two zero.
            var maskLow = Word(words - 3);
            var maskHigh = Word(words - 2);
            pieces.Add(new NdsManifestPiece(
                Word(1),
                Word(2),
                Word(3),
                ((ulong)maskHigh << 32) | maskLow,
                Word(words - 1),
                names.GetValueOrDefault(offset)));
        }

        return pieces;
    }

    /// <summary>
    ///     Reads the exporter's object names, which the overlay stores as two parallel
    ///     pointer arrays right after the table — record pointers then name pointers —
    ///     ordered by name so the game can binary-search them.
    ///
    ///     Three things are derived rather than assumed, and each one validates itself.
    ///     The region's LOAD ADDRESS: a pointer in the first array has to land on one of
    ///     the records, whose offsets are already known, so each candidate base is
    ///     tested by requiring every pointer to hit a distinct record. The array LENGTH:
    ///     it is not the record count — a record with no name is simply absent from the
    ///     index (Sk8land's ov04 indexes 95 of 96) — so the array is walked while its
    ///     pointers keep landing on unused records. And the PAIRING: the names must
    ///     come out in ascending order, which is what the index is for, and which an
    ///     array read one entry too long or too short does not satisfy.
    /// </summary>
    private static IReadOnlyDictionary<int, string> ReadNames(
        byte[] data, uint virtualBase, int at, int stride, int count)
    {
        var empty = new Dictionary<int, string>();
        var arrayAt = at + count * stride;
        if (arrayAt + 8 > data.Length)
            return empty;

        var recordOffsets = new HashSet<int>();
        for (var i = 0; i < count; i++)
            recordOffsets.Add(at + i * stride);

        // Every record offset is a candidate target for the array's first pointer, so
        // each gives a candidate base. Take the one that names the MOST records rather
        // than the first that names any: a wrong base can map one or two pointers by
        // luck, and stopping there would take a two-name reading over a 135-name one.
        var first = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(arrayAt));
        var best = empty;
        foreach (var offset in recordOffsets)
        {
            var candidate = first - (uint)offset;
            if (virtualBase != 0 && candidate != virtualBase)
                continue;

            var mapped = new List<int>(count);
            var used = new HashSet<int>();
            while (mapped.Count < count && arrayAt + mapped.Count * 4 + 4 <= data.Length)
            {
                var pointer = BinaryPrimitives.ReadUInt32LittleEndian(
                    data.AsSpan(arrayAt + mapped.Count * 4));
                var target = (long)pointer - candidate;
                if (target < 0 || target > int.MaxValue
                                || !recordOffsets.Contains((int)target) || !used.Add((int)target))
                {
                    break;
                }

                mapped.Add((int)target);
            }

            // The index can be shorter than the pointers that happen to map: the first
            // word of the NAME array can itself look like a record pointer and extend
            // the walk by one. Longest first, and the ascending-names check is what
            // rejects a length that is off by one rather than a length being assumed.
            for (var length = mapped.Count; length > best.Count; length--)
            {
                var names = ReadNameArray(
                    data, candidate, arrayAt + length * 4, mapped.Take(length).ToList());
                if (names == null)
                    continue;
                best = names;
                break;
            }
        }

        return best;
    }

    private static Dictionary<int, string>? ReadNameArray(
        byte[] data, uint virtualBase, int arrayAt, List<int> records)
    {
        if (arrayAt + records.Count * 4 > data.Length)
            return null;

        var names = new Dictionary<int, string>(records.Count);
        string? previous = null;
        for (var i = 0; i < records.Count; i++)
        {
            var pointer = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(arrayAt + i * 4));
            var target = (long)pointer - virtualBase;
            if (target < 0 || target >= data.Length)
                return null;
            var text = ReadCString(data, (int)target);
            if (text == null)
                return null;
            if (previous != null && string.CompareOrdinal(previous, text) > 0)
                return null;
            previous = text;
            names[records[i]] = text;
        }

        return names;
    }

    private static string? ReadCString(byte[] data, int at)
    {
        var end = at;
        while (end < data.Length && data[end] != 0)
        {
            if (data[end] < 0x20 || data[end] > 0x7E)
                return null;
            end++;
        }

        return end > at && end < data.Length ? System.Text.Encoding.ASCII.GetString(data, at, end - at) : null;
    }
}
