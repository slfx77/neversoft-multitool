using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Core.Formats.N64;

/// <summary>
///     Carves a .z64 ROM into individual typed assets by walking the ROM's own
///     master asset directory (RE'd 2026-08-05 across all four Edge of Reality
///     ports; container story in <see cref="N64RomArchive" />).
///
///     A reassembled stream group opens with the standard sub-file table
///     (payload tables tolerate <c>offs[0] == headerSize + 4</c>: an align-8
///     pad or one trailing zero u32). Entries are classified structurally —
///     the taxonomy was established against the PS1 corpora of the same games
///     (PSX object tables byte-identical after u32 swap, TRG/SFX banks
///     size-matched to the PS1 discs, demo .rec files byte-identical) — and
///     written with platform-suffixed extensions (<c>.psx.n64</c> etc., the
///     same convention as GameCube's <c>.img.ngc</c>). Slot indices are kept
///     in file names because they ARE the games' asset-id space (empty slots
///     are authored placeholders, not decode losses). Model bundles and texture
///     records additionally carry a recovered NAME after the slot where one can
///     be established — for a bundle from its content
///     (<see cref="Mesh.N64.N64BundleNames" />), for a texture from the record's
///     own embedded string.
///
///     Stream roles observed (order varies per game; detection is
///     content-driven, not positional): model bank (4-slot bundles: PSX object
///     table / bounds / u32-byteswapped texture-stripped PSX v4 container /
///     render-bank id), TRG bank ('GRT_' = '_TRG' u32-swapped), N64-native
///     render geometry (left as groupN), texture dictionary (records named
///     'psxtxt_&lt;PS1 texture id&gt;' or by source art name), CI-image bank
///     (3-part records with the 0x00080410 header), misc (.rec demo replays
///     byte-identical to PS1, #ifndef .psh part-name headers, gap/goal
///     tables), SFX cue banks, and raw uncompressed audio groups.
/// </summary>
public static class N64AssetCarver
{
    public readonly record struct CarvedAsset(string Path, byte[] Data);

    private enum Kind
    {
        Unknown,
        Bundle,
        Psx,
        Trg,
        Texture,
        Image,
        Sfx,
        Rec,
        Psh,
        PtrTable
    }

    private readonly record struct Classification(Kind Kind, string Extension, string? Name, int[]? TableOffsets);

    /// <summary>
    ///     Carves the whole ROM via the master directory. False when the ROM
    ///     carries no directory (unknown N64 game) — callers fall back to the
    ///     flat block scan.
    /// </summary>
    public static bool TryCarve(byte[] rom, out List<CarvedAsset> assets)
    {
        assets = [];
        if (!N64RomArchive.TryReadMasterDirectory(rom, out _, out var groups, out var bootTable))
            return false;

        assets.Add(new CarvedAsset("boot.bin", N64RomArchive.ExtractTable(rom, bootTable)));

        var usedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (group.IsCompressed)
            {
                CarveStream(ReassembleStream(rom, group), group.Index, assets, usedDirs);
                continue;
            }

            // Uncompressed group: each leaf is its own payload (wavetables +
            // instrument banks), not a 16 KB slice of a stream. WBK has an
            // exact 16-byte magic; every other raw leaf deliberately remains
            // .bin rather than acquiring a guessed type.
            var dir = UniqueDir(usedDirs, "audio", group.Index);
            var width = SlotWidth(group.Leaves.Count);
            for (var slot = 0; slot < group.Leaves.Count; slot++)
            {
                var (offset, length) = group.Leaves[slot];
                if (length == 0)
                    continue;
                var data = rom[offset..(offset + length)];
                var extension = N64SoundToolsBank.HasWaveMagic(data)
                    ? ".wbk.n64"
                    : ".bin";
                assets.Add(new CarvedAsset(
                    $"{dir}/{slot.ToString($"D{width}")}{extension}",
                    data));
            }
        }

        NameSoundToolsFxBank(assets);
        NameBundles(assets);
        return true;
    }

    /// <summary>
    ///     Gives the one structurally proven Sound Tools effects bank a typed
    ///     suffix. BFX has no file magic, so naming is deliberately deferred
    ///     until the complete carve can supply a unique, fully parsed PTR bank
    ///     and exactly one payload satisfying the complete BFX predicate.
    ///     Any missing, malformed, ambiguous, already-typed, or colliding case
    ///     remains unchanged.
    /// </summary>
    internal static void NameSoundToolsFxBank(List<CarvedAsset> assets)
    {
        var pointerAssets = assets.Where(static asset =>
            N64SoundToolsBank.HasPointerMagic(asset.Data)).ToArray();
        if (pointerAssets.Length != 1)
            return;

        N64SoundToolsPointerBank pointerBank;
        try
        {
            pointerBank = N64SoundToolsBank.ParsePointer(pointerAssets[0].Data);
        }
        catch (InvalidDataException)
        {
            return;
        }

        var candidateIndex = -1;
        for (var i = 0; i < assets.Count; i++)
        {
            if (!N64SoundToolsFxBank.TryParse(assets[i].Data, pointerBank, out _))
                continue;

            if (candidateIndex >= 0)
                return;
            candidateIndex = i;
        }

        if (candidateIndex < 0)
            return;

        var candidate = assets[candidateIndex];
        if (!candidate.Path.EndsWith(".bin", StringComparison.Ordinal))
            return;

        var targetPath = candidate.Path[..^".bin".Length] + ".bfx.n64";
        if (assets.Where((_, index) => index != candidateIndex)
            .Any(asset => string.Equals(asset.Path, targetPath,
                StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        assets[candidateIndex] = candidate with { Path = targetPath };
    }

    /// <summary>
    ///     Renames model-bundle shells from bare slots to <c>{slot}_{name}</c>.
    ///     <para>
    ///         This is a POST-PASS rather than part of <see cref="EmitEntry" />
    ///         because the primary name source is cross-cutting: a trigger names
    ///         a whole level's files at once, and placing that set onto its slot
    ///         run needs every bundle in hand. Per-entry naming could only ever
    ///         reach the content fallback.
    ///     </para>
    /// </summary>
    private static void NameBundles(List<CarvedAsset> assets)
    {
        var bundles = new List<N64BundleNameResolver.Bundle>();
        var indices = new List<int>();
        var triggers = new List<byte[]>();

        for (var i = 0; i < assets.Count; i++)
        {
            var path = assets[i].Path;
            if (path.StartsWith(TriggerRole + "/", StringComparison.Ordinal)
                && path.EndsWith(TrgExtension, StringComparison.Ordinal))
            {
                triggers.Add(assets[i].Data);
                continue;
            }

            if (!path.StartsWith(ModelRole + "/", StringComparison.Ordinal)
                || !path.EndsWith(PsxExtension, StringComparison.Ordinal))
            {
                continue;
            }

            var slash = path.LastIndexOf('/');
            var slot = path[(path.LastIndexOf('/', slash - 1) + 1)..slash];
            bundles.Add(new N64BundleNameResolver.Bundle(slot, assets[i].Data));
            indices.Add(i);
        }

        if (bundles.Count == 0)
            return;

        var names = N64BundleNameResolver.Resolve(bundles, triggers);
        for (var b = 0; b < bundles.Count; b++)
        {
            if (!names.TryGetValue(bundles[b].Slot, out var name))
                continue;

            var index = indices[b];
            var path = assets[index].Path;
            var directory = path[..(path.LastIndexOf('/') + 1)];
            assets[index] = assets[index] with
            {
                Path = $"{directory}{SlotPrefixed(bundles[b].Slot, SanitizeStem(name))}{PsxExtension}"
            };
        }
    }

    /// <summary>Concatenates a compressed group's decoded 16 KB blocks into its logical stream.</summary>
    public static byte[] ReassembleStream(byte[] rom, N64RomArchive.StreamGroup group)
    {
        using var output = new MemoryStream();
        foreach (var (offset, length) in group.Leaves)
        {
            if (length == 0)
                continue;
            var block = rom[offset..(offset + length)];
            output.Write(ErzDecoder.IsErz(block) ? ErzDecoder.Decode(block) : block);
        }

        return output.ToArray();
    }

    /// <summary>Role name of the texture-dictionary stream; its files are slot-prefixed.</summary>
    private const string TextureRole = "textures";

    /// <summary>Role names the bundle-naming post-pass addresses assets by.</summary>
    private const string ModelRole = "models";

    private const string TriggerRole = "triggers";

    private const string PsxExtension = ".psx.n64";
    private const string TrgExtension = ".trg.n64";

    private static void CarveStream(byte[] stream, int groupIndex, List<CarvedAsset> assets, HashSet<string> usedDirs)
    {
        var offsets = TryReadPayloadTable(stream, 0, stream.Length);
        if (offsets == null)
        {
            if (stream.Length > 0)
                assets.Add(new CarvedAsset($"group{groupIndex}.bin", stream));
            return;
        }

        var entries = new List<(int Slot, byte[] Data, Classification Cls)>();
        for (var slot = 0; slot < offsets.Length - 1; slot++)
        {
            var length = offsets[slot + 1] - offsets[slot];
            if (length == 0)
                continue;
            var data = stream[offsets[slot]..offsets[slot + 1]];
            entries.Add((slot, data, Classify(data)));
        }

        var role = DetermineRole(entries);
        var dir = UniqueDir(usedDirs, role ?? $"group{groupIndex}", groupIndex);
        var width = SlotWidth(offsets.Length - 1);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (slot, data, cls) in entries)
        {
            // The size-only .rec test is trusted only inside misc groups.
            var effective = cls.Kind == Kind.Rec && role != "misc"
                ? new Classification(Kind.Unknown, ".bin", null, null)
                : cls;
            EmitEntry(dir, slot, width, data, effective, assets, usedNames, role);
        }
    }

    private static void EmitEntry(
        string dir,
        int slot,
        int width,
        byte[] data,
        Classification cls,
        List<CarvedAsset> assets,
        HashSet<string> usedNames,
        string? role)
    {
        var slotName = slot.ToString($"D{width}");
        if (cls.Kind == Kind.Bundle)
        {
            // 4-slot model bundle; the slot layout is fixed (object table,
            // bounds, PSX container, render-bank link) across all four games.
            //
            // The geometry file is emitted with the bare slot; NameBundles
            // renames it afterwards, once every bundle and every trigger is in
            // hand. The slot stays as a prefix even when a name is found — it is
            // the games' own asset-id space, an unnamed bundle still needs
            // something, and two bundles can hold genuinely identical content
            // (skss_o / skss_o2). The three companions are only ever resolved
            // relative to this file so they keep their fixed names, and the
            // DIRECTORY stays numeric because ArchiveAssetSource disambiguates
            // ~141 identically-named companions by it.
            var t = cls.TableOffsets!;
            assets.Add(new CarvedAsset($"{dir}/{slotName}/{slotName}{PsxExtension}", data[t[2]..t[3]]));
            assets.Add(new CarvedAsset($"{dir}/{slotName}/objects.bin", data[t[0]..t[1]]));
            assets.Add(new CarvedAsset($"{dir}/{slotName}/bounds.bin", data[t[1]..t[2]]));
            assets.Add(new CarvedAsset($"{dir}/{slotName}/renderbank-id.bin", data[t[3]..t[4]]));
            return;
        }

        var sanitized = cls.Name is { Length: > 0 } named ? Sanitize(named) : "";
        var baseName = sanitized.Length > 0 ? sanitized : slotName;

        // Texture records are addressed BY SLOT: a render-bank group names its
        // texture as a u16 index into this dictionary, and empty slots mean the
        // ordinal of a carved file is NOT its slot (THPS3 skips 1,961 holes).
        // Prefixing keeps the index recoverable while preserving the embedded
        // name — including the psxtxt_<id> cross-platform join key.
        if (role == TextureRole && cls.Kind == Kind.Texture)
            baseName = SlotPrefixed(slotName, sanitized);

        if (!usedNames.Add(baseName + cls.Extension))
        {
            baseName = $"{baseName}~{slotName}";
            usedNames.Add(baseName + cls.Extension);
        }

        assets.Add(new CarvedAsset($"{dir}/{baseName}{cls.Extension}", data));
    }

    private static Classification Classify(byte[] data)
    {
        return ClassifyBySignature(data)
               ?? ClassifyByTableShape(data)
               ?? ClassifyByRecordShape(data)
               ?? new Classification(Kind.Unknown, ".bin", null, null);
    }

    private static Classification? ClassifyBySignature(byte[] data)
    {
        if (data.Length >= 8)
        {
            if (StartsWithAscii(data, "GRT_"))
                return new Classification(Kind.Trg, TrgExtension, null, null);

            // u32-byteswapped PSX container: BE word 0x0002000v = LE u16
            // version v in {3,4,6} + u16 magic 0x0002.
            if (IsPsxHead(data, 0))
                return new Classification(Kind.Psx, PsxExtension, null, null);
        }

        if (StartsWithAscii(data, "#ifndef"))
            return new Classification(Kind.Psh, ".psh", PshGuardStem(data), null);
        if (StartsWithAscii(data, "N64 PtrTable"))
            return new Classification(Kind.PtrTable, ".ptr.n64", null, null);
        if (data.Length == 24_064)
            return new Classification(Kind.Rec, ".rec", null, null);
        return null;
    }

    private static Classification? ClassifyByTableShape(byte[] data)
    {
        var table = TryReadPayloadTable(data, 0, data.Length);
        if (table == null)
            return null;

        if (table.Length - 1 == 4 && table[3] - table[2] >= 8 && IsPsxHead(data, table[2]))
            return new Classification(Kind.Bundle, "", null, table);

        if (table.Length - 1 == 3
            && table[1] - table[0] == 28
            && BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(table[0])) == 0x00080410)
        {
            return new Classification(Kind.Image, ".img.n64", null, null);
        }

        return null;
    }

    private static Classification? ClassifyByRecordShape(byte[] data)
    {
        if (TryReadTextureName(data) is { } textureName)
            return new Classification(Kind.Texture, ".tex.n64", textureName, null);
        if (IsSfxCueBank(data))
            return new Classification(Kind.Sfx, ".sfx.n64", null, null);
        return null;
    }

    private static bool IsPsxHead(byte[] data, int position)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position))
            is 0x00020003 or 0x00020004 or 0x00020006;
    }

    /// <summary>
    ///     Texture-dictionary record: NUL-terminated printable name in the
    ///     first 32 bytes + plausible BE u16 dimensions at +0x20.
    /// </summary>
    private static string? TryReadTextureName(byte[] data)
    {
        if (data.Length < 0x40)
            return null;
        var nul = Array.IndexOf(data, (byte)0, 0, 32);
        if (nul < 2)
            return null;
        for (var i = 0; i < nul; i++)
        {
            if (data[i] < 0x20 || data[i] > 0x7E)
                return null;
        }

        var w = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x20));
        var h = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(0x22));
        if (w is < 1 or > 1024 || h is < 1 or > 1024)
            return null;
        return Encoding.ASCII.GetString(data, 0, nul);
    }

    /// <summary>
    ///     SFX cue bank: zero or more complete 16-byte big-endian raw records,
    ///     each with four zero pad bytes, followed immediately by the exact
    ///     0xFFFFFFFF terminator. Classification deliberately shares the
    ///     inspection parser's byte-only predicate: semantic note/pitch ranges
    ///     are not part of the serialized grammar (two THPS2 banks carry note
    ///     bytes above 0x7F).
    /// </summary>
    private static bool IsSfxCueBank(byte[] data)
    {
        return N64SfxCueBank.TryParse(data, out _);
    }

    private static string? DetermineRole(List<(int Slot, byte[] Data, Classification Cls)> entries)
    {
        int CountOf(Kind kind) => entries.Count(entry => entry.Cls.Kind == kind);

        if (CountOf(Kind.Bundle) > 0)
            return ModelRole;
        if (CountOf(Kind.Trg) > 0)
            return TriggerRole;
        if (CountOf(Kind.Texture) >= Math.Max(1, entries.Count / 2))
            return TextureRole;
        if (CountOf(Kind.Image) > 0)
            return "images";
        if (CountOf(Kind.Sfx) > 0)
            return "sfx";
        // Rec is a size-only test, so alone it needs corroboration — a single
        // chance 24,064-byte render blob must not rename a whole group
        // (Spider-Man's render bank ships exactly one).
        if (CountOf(Kind.Psh) > 0 || CountOf(Kind.Rec) >= 2)
            return "misc";
        if (CountOf(Kind.PtrTable) > 0)
            return "audiobanks";
        return null;
    }

    /// <summary>
    ///     Payload-side sub-file table: BE count + count+1 non-decreasing
    ///     offsets. offs[0] may exceed the strict header size by 4 (align-8
    ///     pad / one trailing zero u32); trailing slack after the last offset
    ///     is capped at 8 so arbitrary data can't masquerade as a table.
    /// </summary>
    private static int[]? TryReadPayloadTable(byte[] data, int position, int length)
    {
        if (length < 8)
            return null;
        // Payload tables run bigger than ROM directories — THPS3's texture
        // dictionary declares 4,445 slots.
        var count = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position));
        if (count is < 0 or > 65_535)
            return null;
        var headerSize = 4 + 4 * (count + 1);
        if (headerSize > length)
            return null;

        var offsets = new int[count + 1];
        for (var k = 0; k <= count; k++)
        {
            offsets[k] = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(position + 4 + 4 * k));
            if (k > 0 && (offsets[k] < offsets[k - 1] || offsets[k] > length))
                return null;
        }

        if (offsets[0] != headerSize && offsets[0] != headerSize + 4)
            return null;
        if (length - offsets[count] > 8)
            return null;
        return offsets;
    }

    private static bool StartsWithAscii(byte[] data, string prefix)
    {
        if (data.Length < prefix.Length)
            return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (data[i] != (byte)prefix[i])
                return false;
        }

        return true;
    }

    private static string? PshGuardStem(byte[] data)
    {
        // "#ifndef _ANIMS_FE_PSH" -> "anims_fe"
        var lineEnd = Array.IndexOf(data, (byte)'\n', 0, Math.Min(data.Length, 128));
        if (lineEnd < 0)
            lineEnd = Math.Min(data.Length, 128);
        var line = Encoding.ASCII.GetString(data, 0, lineEnd).Trim();
        var guard = line["#ifndef".Length..].Trim().Trim('_');
        if (guard.EndsWith("_PSH", StringComparison.OrdinalIgnoreCase))
            guard = guard[..^4];
        return guard.Length > 0 ? guard.ToLowerInvariant() : null;
    }

    private static string Sanitize(string name)
    {
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.' ? c : '_');
        return builder.ToString().Trim('.');
    }

    /// <summary>
    ///     <see cref="Sanitize" /> for a name that becomes a file STEM ahead of a
    ///     compound extension. Dots are mapped out entirely rather than merely
    ///     trimmed: <c>MeshTypeDetector</c> matches suffixes longest-first, so a
    ///     stem containing a dot could otherwise manufacture a competing
    ///     extension and route the asset to the wrong reader.
    /// </summary>
    private static string SanitizeStem(string? name)
    {
        if (name is not { Length: > 0 })
            return "";

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
            builder.Append(char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_');
        return builder.ToString();
    }

    /// <summary>
    ///     <c>{slot}_{name}</c>, or the bare slot when nothing named the asset.
    ///     Slot-prefixing keeps the games' own asset-id space recoverable from a
    ///     file name — a carved file's ordinal is NOT its slot once a dictionary
    ///     has holes — while still letting a recovered name identify the row.
    /// </summary>
    private static string SlotPrefixed(string slotName, string name)
    {
        return name.Length > 0 ? $"{slotName}_{name}" : slotName;
    }

    private static string UniqueDir(HashSet<string> usedDirs, string role, int groupIndex)
    {
        var dir = usedDirs.Add(role) ? role : $"{role}{groupIndex}";
        usedDirs.Add(dir);
        return dir;
    }

    private static int SlotWidth(int slotCount)
    {
        return Math.Max(3, Math.Max(slotCount - 1, 0).ToString().Length);
    }
}
