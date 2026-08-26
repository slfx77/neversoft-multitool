using System.Text;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     Carves a Vicarious Visions GBA Tony Hawk ROM into per-level assets so the
///     archive/mesh pipelines can browse it (the N64 <c>.z64</c> route's little
///     sibling). A carved entry <c>levels/&lt;N&gt;_&lt;name&gt;.lvl.gba</c> is the
///     level's 0x15C table record — every field a converter needs (art, palette,
///     collision, name) hangs off it — and <c>rom.gbarom</c> is the ROM itself,
///     carried as a companion because the record's pointers, tile pools, and the
///     collision height functions (<see cref="GbaThumbCpu" /> executes them out of
///     ROM code) all dereference into it.
///
///     <para>Levels are named from the ROM: record <c>+0x00</c> points at the level's
///     name string and <c>+0x04</c> at its location ("Warehouse" / "Troy, NY").</para>
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

    /// <summary>The level-table record stride — also the exact length of a carved
    ///     <c>.lvl.gba</c> entry, which is what the GUI scanner gates on.</summary>
    public const int LevelRecordSize = 0x15C;

    public readonly record struct CarvedLevel(int Index, string Name, string Location, string EntryName);

    /// <summary>
    ///     True when this is a GBA ROM whose Vicarious Visions level table is present
    ///     (currently THPS2; the later carts restructured their level data).
    /// </summary>
    public static bool IsVvLevelRom(ReadOnlySpan<byte> rom)
    {
        // The Nintendo logo's first words gate real GBA ROMs cheaply.
        if (rom.Length < 0xC0 || rom[0x04] != 0x24 || rom[0x05] != 0xFF
            || rom[0x06] != 0xAE || rom[0x07] != 0x51)
            return false;
        return GbaLevelImages.FindLevels(rom).Count > 0;
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
    ///     The carve: one 0x15C record entry per level, plus the ROM companion.
    ///     Returns empty when the ROM is not a supported level ROM.
    /// </summary>
    public static List<(string Path, byte[] Data)> Carve(byte[] rom)
    {
        var result = new List<(string, byte[])>();
        if (!IsVvLevelRom(rom))
            return result;

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
    ///     ROM companion, recovered by content: the record's own bytes appear exactly
    ///     once at the level-table stride, so matching them locates the level without
    ///     any side-channel. Returns -1 when the record is not from this ROM.
    /// </summary>
    public static int FindRecordOffset(ReadOnlySpan<byte> rom, ReadOnlySpan<byte> record)
    {
        if (record.Length != LevelRecordSize)
            return -1;
        var at = rom.IndexOf(record);
        return at;
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
