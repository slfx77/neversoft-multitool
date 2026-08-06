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
    ///     Texture provider keyed by PS1 texture id — the carved records are
    ///     named <c>psxtxt_&lt;8 hex&gt;</c> where the hex IS that id, so the
    ///     same <see cref="MeshChecksumTextureResolver" /> contract the PSX
    ///     material cache already speaks works unchanged. Decoded PNGs are
    ///     cached because the material cache asks once per material key.
    /// </summary>
    public static MeshChecksumTextureResolver BuildTextureProvider(AssetSource source)
    {
        var cache = new Dictionary<uint, byte[]?>();
        return checksum =>
        {
            if (cache.TryGetValue(checksum, out var cached))
                return cached;

            byte[]? png = null;
            var record = TryReadSibling(source, "textures", [$"psxtxt_{checksum:x8}.tex.n64"]);
            if (record != null && N64TexFile.IsN64Texture(record))
            {
                try
                {
                    var texture = N64TexFile.Decode(record);
                    png = ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Rgba);
                }
                catch (InvalidDataException)
                {
                    png = null;
                }
            }

            cache[checksum] = png;
            return png;
        };
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
