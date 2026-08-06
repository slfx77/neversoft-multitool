using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Locates the companions of a carved N64 model bundle. A bundle is a
///     directory <c>models/NNN/</c> holding <c>geometry.psx.n64</c> (the PSX
///     shell), <c>objects.bin</c>, <c>bounds.bin</c> and
///     <c>renderbank-id.bin</c>; the geometry it refers to lives OUTSIDE that
///     directory, in <c>group2/&lt;id&gt;.bin</c>, and its art in
///     <c>textures/psxtxt_&lt;id&gt;.tex.n64</c>.
///     <para>
///         <see cref="AssetSource" />'s companion lookup is same-directory
///         (disk) or flat-basename (archive), so the cross-directory hops are
///         resolved here: on disk by walking up to the carve root, in an
///         archive by full-path lookup with a basename fallback. This mirrors
///         <c>BuildTreeCompanionLocator</c>'s role for the PS2 scene formats.
///     </para>
/// </summary>
public static class N64ModelCompanions
{
    private const string RenderBankIdName = "renderbank-id.bin";
    private static readonly string[] BundleAnchors = ["group2", "textures"];

    /// <summary>
    ///     Reads the bundle's render-bank index (BE u32 in
    ///     <c>renderbank-id.bin</c>), or null when the companion is absent.
    ///     The index is into <c>group2/</c>'s slot numbering and is unique per
    ///     bundle across every ROM measured.
    /// </summary>
    public static uint? TryReadRenderBankId(AssetSource source)
    {
        var data = source.TryReadCompanion(RenderBankIdName);
        return data is { Length: >= 4 } ? BinaryPrimitives.ReadUInt32BigEndian(data) : null;
    }

    /// <summary>
    ///     Reads the render-bank record this bundle points at, or null when
    ///     either the id or the record is missing.
    /// </summary>
    public static byte[]? TryReadRenderBank(AssetSource source)
    {
        var id = TryReadRenderBankId(source);
        return id == null ? null : TryReadSibling(source, "group2", CandidateSlotNames(id.Value));
    }

    /// <summary>
    ///     A resolved texture plus its alpha profile. N64 art carries real
    ///     transparency — RGBA5551 has a 1-bit alpha and CI palettes mark
    ///     transparent entries with A=0 — which is how wheels, steering wheels
    ///     and foliage are cut out of their quads.
    /// </summary>
    public sealed record N64ResolvedTexture(
        string Name, int Width, int Height, byte[] Png, bool HasCutout, bool HasGraduatedAlpha);

    /// <summary>
    ///     Resolves textures by dictionary slot, caching decoded PNGs (the
    ///     material cache asks once per material, but many groups share a
    ///     texture). Slot addressing is required rather than the
    ///     <c>psxtxt_&lt;id&gt;</c> checksum: hundreds of records per ROM are
    ///     art-named and carry no checksum at all, so a checksum key can never
    ///     cover the dictionary. The carver slot-prefixes texture file names,
    ///     which is what makes the lookup possible — the ordinal of a file is
    ///     NOT its slot when the dictionary has holes.
    /// </summary>
    public static Func<int, N64ResolvedTexture?> BuildTextureProvider(AssetSource source)
    {
        var cache = new Dictionary<int, N64ResolvedTexture?>();
        return slot =>
        {
            if (cache.TryGetValue(slot, out var cached))
                return cached;

            N64ResolvedTexture? resolved = null;
            var record = TryReadTextureSlot(source, slot);
            if (record != null && N64TexFile.IsN64Texture(record))
            {
                try
                {
                    var texture = N64TexFile.Decode(record);
                    var (cutout, graduated) = ClassifyAlpha(texture.Rgba);
                    resolved = new N64ResolvedTexture(
                        texture.Name ?? $"tex_{slot:D4}",
                        texture.Width,
                        texture.Height,
                        ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Rgba),
                        cutout,
                        graduated);
                }
                catch (InvalidDataException)
                {
                    resolved = null;
                }
            }

            cache[slot] = resolved;
            return resolved;
        };
    }

    /// <summary>
    ///     Splits a decoded image into "has fully transparent texels" (a
    ///     cutout, which wants alpha testing) and "has partial alpha" (which
    ///     wants blending). The N64's 1-bit RGBA5551 and A=0 palette entries
    ///     produce the former; IA formats can produce the latter.
    /// </summary>
    private static (bool Cutout, bool Graduated) ClassifyAlpha(byte[] rgba)
    {
        var cutout = false;
        var graduated = false;
        for (var i = 3; i < rgba.Length; i += 4)
        {
            var a = rgba[i];
            if (a == 0)
                cutout = true;
            else if (a < 255)
                graduated = true;
        }

        return (cutout, graduated);
    }

    /// <summary>
    ///     Reads the texture record at a dictionary slot. Files are named
    ///     <c>&lt;slot&gt;_&lt;embedded name&gt;.tex.n64</c>, so the slot is a
    ///     zero-padded prefix; widths vary per ROM, hence the candidate set.
    /// </summary>
    public static byte[]? TryReadTextureSlot(AssetSource source, int slot)
    {
        if (slot <= 0)
            return null;

        string[] prefixes =
        [
            $"{slot:D4}_", $"{slot:D3}_", $"{slot:D5}_", $"{slot:D2}_", $"{slot}_"
        ];
        return TryReadSiblingByPrefix(source, "textures", prefixes);
    }

    /// <summary>
    ///     The carver zero-pads slot names to a per-ROM width, so try the
    ///     observed widths rather than assuming one.
    /// </summary>
    private static string[] CandidateSlotNames(uint id)
    {
        return [$"{id:D3}.bin", $"{id:D4}.bin", $"{id:D2}.bin", $"{id}.bin"];
    }

    /// <summary>
    ///     Sibling-directory lookup by file-name PREFIX, for slot-addressed
    ///     records whose full name also carries the embedded art name.
    /// </summary>
    private static byte[]? TryReadSiblingByPrefix(
        AssetSource source,
        string directory,
        IReadOnlyList<string> prefixes)
    {
        if (source is ArchiveAssetSource archive)
        {
            foreach (var entry in archive.Backend.Entries)
            {
                if (!entry.Directory.Equals(directory, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (prefixes.Any(p => entry.Name.StartsWith(p, StringComparison.Ordinal)))
                    return archive.Backend.ReadEntryBytes(entry);
            }

            return null;
        }

        var root = TryFindCarveRoot(source);
        if (root == null)
            return null;

        var dir = Path.Combine(root, directory);
        if (!Directory.Exists(dir))
            return null;

        foreach (var prefix in prefixes)
        {
            var match = Directory.EnumerateFiles(dir, prefix + "*").FirstOrDefault();
            if (match != null)
                return File.ReadAllBytes(match);
        }

        return null;
    }

    /// <summary>
    ///     Finds a file in a SIBLING directory of the bundle. Archive sources
    ///     get a full-path lookup (entry directories are preserved by the
    ///     carve, e.g. <c>group2</c>/<c>textures</c>); disk sources walk up
    ///     from <c>models/NNN/</c> to the carve root. Falls back to the flat
    ///     basename lookup, which is safe here because the carved basenames are
    ///     unique outside their own directory.
    /// </summary>
    private static byte[]? TryReadSibling(AssetSource source, string directory, IReadOnlyList<string> names)
    {
        if (source is ArchiveAssetSource archive)
        {
            foreach (var name in names)
            {
                var entry = archive.Backend.FindByPath($"{directory}/{name}");
                if (entry != null)
                    return archive.Backend.ReadEntryBytes(entry);
            }
        }

        var root = TryFindCarveRoot(source);
        if (root != null)
        {
            foreach (var name in names)
            {
                var path = Path.Combine(root, directory, name);
                if (File.Exists(path))
                    return File.ReadAllBytes(path);
            }
        }

        foreach (var name in names)
        {
            var flat = source.TryReadCompanion(name);
            if (flat != null)
                return flat;
        }

        return null;
    }

    /// <summary>
    ///     Walks up from the bundle directory looking for the carve root — the
    ///     directory that holds <c>group2/</c> and <c>textures/</c>. Bounded to
    ///     a few levels: the real depth is exactly two (<c>models/NNN</c>).
    /// </summary>
    private static string? TryFindCarveRoot(AssetSource source)
    {
        var path = source.FileSystemPath;
        if (string.IsNullOrEmpty(path))
            return null;

        var dir = Path.GetDirectoryName(path);
        for (var level = 0; level < 4 && !string.IsNullOrEmpty(dir); level++)
        {
            if (BundleAnchors.Any(anchor => Directory.Exists(Path.Combine(dir, anchor))))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        return null;
    }
}
