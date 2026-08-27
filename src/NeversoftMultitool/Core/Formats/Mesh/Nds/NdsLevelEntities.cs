using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     One gameplay entity a level places: the model set it draws, where it stands,
///     and the rotation the file states but this codebase does not apply.
/// </summary>
/// <param name="ModelName">The model set's authored name — <c>skate_s</c>, <c>trashcan</c>.</param>
/// <param name="Position">World position, in the same space as the level's geometry.</param>
/// <param name="RotationDegrees">
///     The record's rotation property when it carries one. RECORDED, NOT APPLIED: the
///     values are degrees (100% land in [-180, 360] and almost all are multiples of 5)
///     but which axis they turn about is not established, and a wrong axis would be
///     worse than none.
/// </param>
public sealed record NdsEntityPlacement(
    string ModelName, uint SetId, Vector3 Position, float? RotationDegrees);

/// <summary>
///     Reads a level's placed entities out of its <c>&lt;Name&gt;_Collision.prp</c>.
///
///     A <c>.prp</c> record is a property list of <c>{u32 tag, i32 value}</c> pairs
///     ending at <c>tag == 0</c>, and a value that is a reference is a signed offset
///     from the address of its OWN tag word. Parsing that way consumes every record
///     exactly against the header's offset table, which is what proves the grammar —
///     one wrong width desynchronises immediately.
///
///     The reference is to a model set by NAME, as a plain C string. That is why
///     searching these files for an entity's id as a bare u32 correctly found nothing:
///     the id is never stored, only the name whose CRC-32 the id IS.
///
///     <b>Tag numbers are per game</b> — the three carts compiled three separate
///     property enums, so the same property is tag 9 in one build and 5 in another.
///     Rather than table them, this DERIVES them per file and validates as it goes:
///     the name tag is the one whose targets re-hash onto model sets the container
///     actually holds, and the position tag is the one whose targets land inside the
///     level's own geometry. A tag that does neither scores at chance and is ignored,
///     so an unseen build is read correctly or not at all rather than misread.
/// </summary>
public static class NdsLevelEntities
{
    /// <summary>
    ///     The position scale. A <c>.prp</c> coordinate is <c>i32</c> 20.12 in the
    ///     collision world, whose vertices are <c>s16</c> at 4x that unit and 1/32 of a
    ///     geometry unit — so a placement reaches geometry space at <c>/32768</c>.
    ///     Measured: every Sk8land and Downhill Jam entity, and 787 of Proving
    ///     Ground's 795, then land inside their own level's box.
    /// </summary>
    public const float PositionScale = 32768f;

    private const int MinimumTagEvidence = 8;
    private const float MinimumPurity = 0.9f;

    /// <summary>The <c>.prp</c> file name for a level's model-set name.</summary>
    public static string? DataFileFor(string levelSetName)
    {
        if (!NdsSetNames.IsLevel(levelSetName))
            return null;
        return levelSetName[..^NdsSetNames.LevelSuffix.Length] + "_Collision.prp";
    }

    /// <summary>
    ///     Reads every placement the file states. Returns empty for anything that is
    ///     not a well-formed <c>.prp</c>, or whose tags cannot be identified against
    ///     <paramref name="knownSets" />.
    /// </summary>
    /// <param name="data">The <c>.prp</c> bytes.</param>
    /// <param name="knownSetIds">
    ///     The container's own model-set ids. The gate is the loader's own rule — a
    ///     set's id IS the CRC-32 of its name — so a candidate string is HASHED rather
    ///     than looked up. That matters: eight Proving Ground entities name sets whose
    ///     names appear only here and never in the cart's code, so a name harvested
    ///     from ARM9 would not have found them. The .prp is a name source in its own
    ///     right.
    /// </param>
    public static IReadOnlyList<NdsEntityPlacement> Parse(
        ReadOnlySpan<byte> data,
        IReadOnlyCollection<uint> knownSetIds)
    {
        ArgumentNullException.ThrowIfNull(knownSetIds);
        var known = knownSetIds as HashSet<uint> ?? [.. knownSetIds];
        if (!TryReadRecords(data, out var records, out var poolStart))
            return [];

        var nameTag = FindNameTag(data, records, poolStart, known);
        if (nameTag == null)
            return [];
        var positionTag = FindPositionTag(data, records, poolStart, nameTag.Value);
        if (positionTag == null)
            return [];
        var rotationTag = FindRotationTag(records, nameTag.Value);

        var placements = new List<NdsEntityPlacement>();
        foreach (var record in records)
        {
            string? name = null;
            uint setId = 0;
            Vector3? position = null;
            float? rotation = null;
            foreach (var (tag, value, at) in record)
            {
                if (tag == nameTag && TryReadString(data, at + value, out var text)
                                   && known.Contains(NdsSetNames.Hash(text)))
                {
                    name = text;
                    setId = NdsSetNames.Hash(text);
                }
                else if (tag == positionTag && TryReadVector(data, at + value, out var vector))
                {
                    position = vector;
                }
                else if (rotationTag != null && tag == rotationTag)
                {
                    rotation = value;
                }
            }

            if (name != null && position != null)
                placements.Add(new NdsEntityPlacement(name, setId, position.Value, rotation));
        }

        return placements;
    }

    /// <summary>
    ///     Records as property lists, plus where the VALUE POOL begins.
    ///
    ///     False when the header shape or the record tiling does not hold — the offset
    ///     table says where each record ends, so a grammar that does not land there
    ///     exactly is the wrong grammar.
    ///
    ///     The pool boundary is not bookkeeping, it is a discriminator. A property
    ///     whose value is a reference points either at another RECORD or into the
    ///     pool, and only pool targets are data. Without that split a record reference
    ///     looks like a position: it lands on a record, whose own first property is
    ///     often a position, so it decodes as a plausible in-level vec3. That is
    ///     exactly what made one Sk8land level pick its reference tag over its
    ///     position tag — the decoy scored 71 in-box hits against the real tag's 66.
    /// </summary>
    private static bool TryReadRecords(
        ReadOnlySpan<byte> data,
        out List<List<(uint Tag, int Value, int At)>> records,
        out int poolStart)
    {
        records = [];
        poolStart = 0;
        if (data.Length < 0x18
            || data[0] != (byte)'P' || data[1] != (byte)'F' || data[2] != (byte)'P' || data[3] != (byte)'F')
        {
            return false;
        }

        var count = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var dataEnd = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
        if (count <= 0 || count > 1 << 20 || dataEnd > data.Length)
            return false;

        var tableEnd = 0x14 + count * 4;
        if (tableEnd > data.Length)
            return false;

        var offsets = new int[count];
        for (var i = 0; i < count; i++)
        {
            offsets[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(0x14 + i * 4)..]);
            if (offsets[i] < tableEnd || offsets[i] >= dataEnd)
                return false;
            if (i > 0 && offsets[i] <= offsets[i - 1])
                return false;
        }

        if (offsets[0] != tableEnd)
            return false;

        var parsed = new List<List<(uint, int, int)>>(count);
        for (var i = 0; i < count; i++)
        {
            var at = offsets[i];
            var limit = i + 1 < count ? offsets[i + 1] : dataEnd;
            var props = new List<(uint, int, int)>();
            while (true)
            {
                if (at + 8 > limit)
                    return false;
                var tag = BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
                var value = BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]);
                if (tag == 0)
                {
                    at += 8;
                    break;
                }

                props.Add((tag, value, at));
                at += 8;
            }

            // Each record must consume exactly up to the next one's start.
            if (i + 1 < count && at != limit)
                return false;
            parsed.Add(props);
            poolStart = at;
        }

        records = parsed;
        return true;
    }

    private static uint? FindNameTag(
        ReadOnlySpan<byte> data,
        List<List<(uint Tag, int Value, int At)>> records,
        int poolStart,
        HashSet<uint> knownSetIds)
    {
        var hits = new Dictionary<uint, int>();
        var total = new Dictionary<uint, int>();
        foreach (var record in records)
        foreach (var (tag, value, at) in record)
        {
            total[tag] = total.GetValueOrDefault(tag) + 1;
            if (at + value >= poolStart
                && TryReadString(data, at + value, out var text)
                && knownSetIds.Contains(NdsSetNames.Hash(text)))
                hits[tag] = hits.GetValueOrDefault(tag) + 1;
        }

        return BestTag(hits, total);
    }

    /// <summary>
    ///     The position property, identified WITHOUT any external oracle: it is the
    ///     pool-pointing property present on the most entity records.
    ///
    ///     An earlier version scored candidates by how many of their targets landed
    ///     inside the level's own geometry box. That needed the level in hand, and it
    ///     was not even the discriminating fact — a reference tag points at a record
    ///     whose own first property is a position, so it decodes as an in-level vec3
    ///     too, and in one Sk8land level it out-scored the real tag. Once references
    ///     are excluded by the pool boundary, plain coverage settles it: a placed
    ///     entity always has a position. Measured: this picks the right tag for
    ///     20 of 20 level files across the three carts, with no box at all.
    /// </summary>
    private static uint? FindPositionTag(
        ReadOnlySpan<byte> data,
        List<List<(uint Tag, int Value, int At)>> records,
        int poolStart,
        uint nameTag)
    {
        var hits = new Dictionary<uint, int>();
        var total = new Dictionary<uint, int>();
        foreach (var record in records)
        {
            // Only records that name a model set: a level also stores waypoint and
            // path positions, which are not placements.
            if (!record.Any(p => p.Tag == nameTag))
                continue;
            var counted = new HashSet<uint>();
            foreach (var (tag, value, at) in record)
            {
                if (tag == nameTag)
                    continue;
                total[tag] = total.GetValueOrDefault(tag) + 1;
                if (at + value < poolStart || !TryReadVector(data, at + value, out _))
                    continue;
                // Coverage is per RECORD: a tag appearing twice in one record is one
                // record's worth of evidence, not two.
                if (counted.Add(tag))
                    hits[tag] = hits.GetValueOrDefault(tag) + 1;
            }
        }

        return BestTag(hits, total);
    }

    /// <summary>
    ///     The rotation property, identified by its VALUES rather than by a target:
    ///     it is an immediate, and every one is a whole number of degrees.
    /// </summary>
    private static uint? FindRotationTag(
        List<List<(uint Tag, int Value, int At)>> records, uint nameTag)
    {
        var hits = new Dictionary<uint, int>();
        var total = new Dictionary<uint, int>();
        foreach (var record in records)
        {
            if (!record.Any(p => p.Tag == nameTag))
                continue;
            foreach (var (tag, value, _) in record)
            {
                if (tag == nameTag)
                    continue;
                total[tag] = total.GetValueOrDefault(tag) + 1;
                if (value is >= -180 and <= 360 && value % 5 == 0)
                    hits[tag] = hits.GetValueOrDefault(tag) + 1;
            }
        }

        return BestTag(hits, total);
    }

    private static uint? BestTag(Dictionary<uint, int> hits, Dictionary<uint, int> total)
    {
        uint? best = null;
        var bestHits = 0;
        foreach (var (tag, hit) in hits)
        {
            if (hit < MinimumTagEvidence || hit < total[tag] * MinimumPurity)
                continue;
            if (hit <= bestHits)
                continue;
            bestHits = hit;
            best = tag;
        }

        return best;
    }

    private static bool TryReadVector(ReadOnlySpan<byte> data, int at, out Vector3 vector)
    {
        vector = default;
        if (at < 0 || at + 12 > data.Length)
            return false;
        vector = new Vector3(
            BinaryPrimitives.ReadInt32LittleEndian(data[at..]) / PositionScale,
            BinaryPrimitives.ReadInt32LittleEndian(data[(at + 4)..]) / PositionScale,
            BinaryPrimitives.ReadInt32LittleEndian(data[(at + 8)..]) / PositionScale);
        return true;
    }

    private static bool TryReadString(ReadOnlySpan<byte> data, int at, out string text)
    {
        text = string.Empty;
        // A real string start is preceded by the NUL that ends the previous one.
        if (at <= 0 || at >= data.Length || data[at - 1] != 0)
            return false;

        var end = at;
        while (end < data.Length && data[end] != 0)
        {
            var c = data[end];
            if (c < 0x20 || c > 0x7E)
                return false;
            end++;
        }

        if (end == at || end >= data.Length || end - at > 64)
            return false;
        text = Encoding.ASCII.GetString(data[at..end]);
        return true;
    }
}
