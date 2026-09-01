using System.Text;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Carves a Vicarious Visions GBA Tony Hawk ROM into per-level assets so the
///     archive/mesh pipelines can browse it (the N64 <c>.z64</c> route's little
///     sibling). A carved entry <c>levels/&lt;N&gt;_&lt;name&gt;.lvl.gba</c> is the
///     level's native table record — every field a converter needs hangs off it —
///     and <c>rom.gbarom</c> is the ROM itself,
///     carried as a companion because the record's pointers, tile pools, and the
///     collision height functions (<see cref="GbaThumbCpu" /> executes them out of
///     ROM code) all dereference into it.
///
///     <para>THPS2 levels are named from the ROM: record <c>+0x00</c> points at the
///     level's name string and <c>+0x04</c> at its location ("Warehouse" / "Troy,
///     NY"). The later record families do not expose an equivalent decoded name
///     field, so their carved entries use stable table indices.</para>
/// </summary>
public static class GbaLevelCarver
{
    private const uint RomBase = 0x08000000;

    /// <summary>The ROM companion's file name; every carved level resolves against it.</summary>
    public const string RomEntryName = "rom.gbarom";

    /// <summary>The companion's carved path — inside <c>levels/</c> so a loose
    ///     extraction keeps it a same-directory sibling of the level records.</summary>
    public const string RomEntryPath = "levels/" + RomEntryName;

    /// <summary>Carved level records carry this suffix (routes to the mesh pipeline).</summary>
    public const string LevelSuffix = ".lvl.gba";

    /// <summary>THPS2's level-table record stride and carved-record length.
    ///     THPS3 and the later games expose their own native stride constants.</summary>
    public const int LevelRecordSize = 0x15C;

    public readonly record struct CarvedLevel(int Index, string Name, string Location, string EntryName);

    /// <summary>
    ///     True when this is a GBA ROM whose Vicarious Visions level table is present
    ///     (THPS2 through American Sk8land; each generation keeps its native
    ///     level-record shape).
    /// </summary>
    public static bool IsVvLevelRom(ReadOnlySpan<byte> rom)
    {
        // The Nintendo logo's first words gate real GBA ROMs cheaply.
        if (rom.Length < 0xC0 || rom[0x04] != 0x24 || rom[0x05] != 0xFF
            || rom[0x06] != 0xAE || rom[0x07] != 0x51)
            return false;
        return GbaLevelImages.FindLevels(rom).Count > 0
               || GbaThps3LevelArt.FindLevels(rom).Count > 0
               || GbaLaterLevelArt.FindLevels(rom).Count > 0;
    }

    /// <summary>Path-based gate for the archive detector / unpacker.</summary>
    public static bool IsVvLevelRom(string path)
    {
        try
        {
            return Path.GetExtension(path).Equals(".gba", StringComparison.OrdinalIgnoreCase)
                   && File.Exists(path) && IsVvLevelRom(File.ReadAllBytes(path));
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Archive classification label, or null when not a supported level ROM.</summary>
    public static string? ClassifyRom(string path) =>
        IsVvLevelRom(path) ? "GBA ROM (Vicarious Visions)" : null;

    /// <summary>Entry listing for the CLI/tab routes (mirrors N64RomArchive.GetFileList).</summary>
    public static List<Archives.ArchiveEntry> GetFileList(string path)
    {
        var assets = Carve(File.ReadAllBytes(path));
        var entries = new List<Archives.ArchiveEntry>(assets.Count);
        for (var i = 0; i < assets.Count; i++)
        {
            var (assetPath, data) = assets[i];
            var slash = assetPath.LastIndexOf('/');
            entries.Add(new Archives.ArchiveEntry
            {
                Directory = slash > 0 ? assetPath[..slash] : "",
                Name = slash > 0 ? assetPath[(slash + 1)..] : assetPath,
                Size = data.Length,
                Offset = i
            });
        }

        return entries;
    }

    /// <summary>Extracts the carve to disk (the unpacker's direct route).</summary>
    public static void ExtractFiles(string path, string outputDir, CancellationToken ct = default)
    {
        var rom = File.ReadAllBytes(path);
        foreach (var (entryPath, data) in Carve(rom))
        {
            ct.ThrowIfCancellationRequested();
            var target = Path.Combine(outputDir, entryPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
    }

    /// <summary>The carved level list (names read from the ROM's own strings).</summary>
    public static List<CarvedLevel> ListLevels(ReadOnlySpan<byte> rom)
    {
        var thps3 = GbaThps3LevelArt.FindLevels(rom);
        if (thps3.Count > 0)
        {
            var named = new List<CarvedLevel>(thps3.Count);
            foreach (var level in thps3)
                named.Add(new CarvedLevel(
                    level.Index, $"level{level.Index}", "", $"levels/{level.Index}_level{LevelSuffix}"));
            return named;
        }

        var later = GbaLaterLevelArt.FindLevels(rom);
        if (later.Count > 0)
        {
            // The later cartridges' level record carries no name string, so the
            // carve numbers them rather than inventing one.
            var named = new List<CarvedLevel>(later.Count);
            foreach (var level in later)
                named.Add(new CarvedLevel(
                    level.Index, $"level{level.Index}", "", $"levels/{level.Index}_level{LevelSuffix}"));
            return named;
        }

        var levels = GbaLevelImages.FindLevels(rom);
        var result = new List<CarvedLevel>(levels.Count);
        for (var i = 0; i < levels.Count; i++)
        {
            var trueRecord = (int)(levels[i].RecordAddress - RomBase) - 0x144;
            var name = TryReadString(rom, trueRecord) ?? $"level{i}";
            var location = TryReadString(rom, trueRecord + 4) ?? "";
            result.Add(new CarvedLevel(i, name, location, $"levels/{i}_{Slug(name)}{LevelSuffix}"));
        }

        return result;
    }

    /// <summary>
    ///     The carve: one generation-native record per level, plus the ROM companion.
    ///     Returns empty when the ROM is not a supported level ROM.
    /// </summary>
    public static List<(string Path, byte[] Data)> Carve(byte[] rom)
    {
        var result = new List<(string, byte[])>();
        if (!IsVvLevelRom(rom))
            return result;

        // THPS3: the older all-8bpp art record. The same 0x70-byte parent also
        // carries its collision-complex pointers, so one carved record routes both
        // the authored surface and the ROM-evaluated level mesh.
        var thps3 = GbaThps3LevelArt.FindLevels(rom);
        if (thps3.Count > 0)
        {
            var carvedThps3 = ListLevels(rom);
            for (var i = 0; i < thps3.Count && i < carvedThps3.Count; i++)
            {
                var offset = thps3[i].LevelRecordOffset;
                if (offset < 0 || offset + GbaThps3LevelArt.LevelRecordStride > rom.Length)
                    continue;
                result.Add((carvedThps3[i].EntryName,
                    rom.AsSpan(offset, GbaThps3LevelArt.LevelRecordStride).ToArray()));
            }

            result.Add((RomEntryPath, rom));
            return result;
        }

        // THPS4 / THUG / THUG2 / Sk8land: a different art record. The carve is
        // the level records plus the ROM companion.
        var later = GbaLaterLevelArt.FindLevels(rom);
        if (later.Count > 0)
        {
            var carvedLater = ListLevels(rom);
            for (var i = 0; i < later.Count && i < carvedLater.Count; i++)
            {
                var offset = later[i].ArtRecordOffset;
                if (offset < 0 || offset + GbaLaterLevelArt.ArtRecordStride > rom.Length)
                    continue;
                result.Add((carvedLater[i].EntryName,
                    rom.AsSpan(offset, GbaLaterLevelArt.ArtRecordStride).ToArray()));
            }

            result.Add((RomEntryPath, rom));
            return result;
        }

        var levels = GbaLevelImages.FindLevels(rom);
        var carved = ListLevels(rom);
        for (var i = 0; i < carved.Count; i++)
        {
            var trueRecord = (int)(levels[i].RecordAddress - RomBase) - 0x144;
            if (trueRecord < 0 || trueRecord + LevelRecordSize > rom.Length)
                continue;
            result.Add((carved[i].EntryName, rom.AsSpan(trueRecord, LevelRecordSize).ToArray()));
        }

        // The 3D character models: one entry per roster character (the 0x4C record;
        // the shared morph-target mesh + that character's colours resolve through
        // the ROM companion — see GbaSkaterModel).
        var model = GbaSkaterModel.TryLocate(rom);
        if (model != null)
        {
            for (var i = 0; i < model.CharacterCount; i++)
            {
                var name = GbaSkaterModel.TryGetCharacterName(rom, model, i) ?? $"character{i}";
                var record = model.CharacterTableOffset + i * GbaSkaterModel.CharacterRecordSize;
                if (record + GbaSkaterModel.CharacterRecordSize > rom.Length)
                    continue;
                result.Add(($"models/{i:D2}_{Slug(name)}.chr.gba",
                    rom.AsSpan(record, GbaSkaterModel.CharacterRecordSize).ToArray()));
            }

            // A second reference to the same ROM buffer so loose extractions keep a
            // same-directory companion for the model records too.
            result.Add(("models/" + RomEntryName, rom));
        }

        // One shared copy of the ROM; the level records dereference into it.
        result.Add((RomEntryPath, rom));
        return result;
    }

    /// <summary>
    ///     The true-record file offset a carved <c>.lvl.gba</c> record occupies in its
    ///     ROM companion, recovered by matching its content against the generation's
    ///     structurally discovered level table. Returns -1 when no matching native
    ///     record belongs to this ROM.
    /// </summary>
    public static int FindRecordOffset(ReadOnlySpan<byte> rom, ReadOnlySpan<byte> record)
    {
        if (record.Length == GbaLaterLevelArt.ArtRecordStride)
        {
            foreach (var level in GbaLaterLevelArt.FindLevels(rom))
            {
                if (!rom.Slice(level.ArtRecordOffset, record.Length).SequenceEqual(record))
                    continue;
                return level.LevelRecordOffset;
            }

            return -1;
        }

        if (record.Length == GbaThps3LevelArt.LevelRecordStride)
        {
            foreach (var level in GbaThps3LevelArt.FindLevels(rom))
            {
                if (!rom.Slice(level.LevelRecordOffset, record.Length).SequenceEqual(record))
                    continue;
                return level.LevelRecordOffset;
            }

            return -1;
        }

        return record.Length == LevelRecordSize ? rom.IndexOf(record) : -1;
    }

    private static string? TryReadString(ReadOnlySpan<byte> rom, int pointerOffset)
    {
        if (pointerOffset < 0 || pointerOffset + 4 > rom.Length)
            return null;
        var address = (uint)(rom[pointerOffset] | (rom[pointerOffset + 1] << 8)
                                                | (rom[pointerOffset + 2] << 16) | (rom[pointerOffset + 3] << 24));
        if (address < RomBase || address >= RomBase + (uint)rom.Length)
            return null;
        var start = (int)(address - RomBase);
        var end = start;
        while (end < rom.Length && rom[end] != 0 && end - start < 40)
        {
            if (rom[end] < 0x20 || rom[end] > 0x7E)
                return null;
            end++;
        }

        return end > start ? Encoding.ASCII.GetString(rom[start..end]) : null;
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
            sb.Append(char.IsAsciiLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
