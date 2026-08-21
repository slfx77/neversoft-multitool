using System.Buffers.Binary;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture.N64;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Locates the companions of a carved N64 model bundle. A bundle is a
///     directory <c>models/NNN/</c> holding <c>NNN_&lt;name&gt;.psx.n64</c> (the
///     PSX shell), <c>objects.bin</c>, <c>bounds.bin</c> and
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
    private const string BoundsName = "bounds.bin";
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
    ///     Reads the bundle's <c>bounds.bin</c>, or null when it is absent.
    /// </summary>
    public static byte[]? TryReadBounds(AssetSource source)
    {
        return source.TryReadCompanion(BoundsName);
    }

    /// <summary>
    ///     Largest bounding radius across the bundle's meshes — the world-scale
    ///     feature <see cref="N64BundleClassifier" /> keys on. Null when the
    ///     companion is absent; 0 for an authored-empty stub.
    /// </summary>
    public static float? TryReadMaxBoundsRadius(AssetSource source)
    {
        var data = TryReadBounds(source);
        return data == null ? null : N64BoundsFile.MaxRadius(data);
    }

    /// <summary>
    ///     Reads the ROM's single light rig out of the carved <c>boot.bin</c>,
    ///     or null when the boot image is absent. Every bundle in one ROM shares
    ///     it — the rig is uploaded once at startup and never rewritten.
    /// </summary>
    public static N64LightRig? TryReadLightRig(AssetSource source)
    {
        var boot = TryReadSibling(source, string.Empty, ["boot.bin"]);
        return boot == null ? null : N64LightRig.TryParse(boot);
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
        string Name, int Width, int Height, byte[] Png, bool HasCutout, bool HasGraduatedAlpha)
    {
        /// <summary>
        ///     The authored render state composites this texture with the
        ///     framebuffer even when its decoded alpha is only binary or solid.
        /// </summary>
        public bool ForcesBlend { get; init; }

        /// <summary>
        ///     Authored RDP clamp/mirror state, carried through to the glTF
        ///     sampler. Wrap belongs to the dictionary SLOT, not to a material,
        ///     so it needs no extra material-cache key.
        /// </summary>
        public ModelTextureWrap WrapU { get; init; } = ModelTextureWrap.Repeat;

        public ModelTextureWrap WrapV { get; init; } = ModelTextureWrap.Repeat;
    }

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
                    // glTF embeds one source image and lets the renderer build
                    // its sampling pyramid. Keep that image at authored level
                    // zero; stored N64 mips are disk-extraction companions,
                    // not additional model materials.
                    var (cutout, graduated, forcesBlend) = ClassifyAlpha(
                        texture.Format,
                        texture.RenderClass,
                        texture.Rgba);
                    resolved = new N64ResolvedTexture(
                        texture.Name ?? $"tex_{slot:D4}",
                        texture.Width,
                        texture.Height,
                        ImageWriter.WritePngToMemory(texture.Width, texture.Height, texture.Rgba),
                        cutout,
                        graduated)
                    {
                        ForcesBlend = forcesBlend,
                        WrapU = ToModelWrap(texture.WrapS),
                        WrapV = ToModelWrap(texture.WrapT)
                    };
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

    private static ModelTextureWrap ToModelWrap(N64TexFile.N64TextureWrap wrap)
    {
        return wrap switch
        {
            N64TexFile.N64TextureWrap.Clamp => ModelTextureWrap.ClampToEdge,
            N64TexFile.N64TextureWrap.Mirror => ModelTextureWrap.MirroredRepeat,
            _ => ModelTextureWrap.Repeat
        };
    }

    /// <summary>
    ///     Splits a decoded image into "has fully transparent texels" (a
    ///     cutout, which wants alpha testing) and "has partial alpha" (which
    ///     wants blending). Existing non-intensity formats retain that pixel
    ///     policy. I4/I8 additionally use the authored render class: opaque
    ///     surfaces ignore their physical A=I channel, texture coverage maps
    ///     every non-full intensity to a mask, and translucent surfaces retain
    ///     their profile plus a separate forced-blend signal.
    /// </summary>
    internal static (bool Cutout, bool Graduated, bool ForcesBlend) ClassifyAlpha(
        string format,
        N64TexFile.N64TextureRenderClass renderClass,
        byte[] rgba)
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

        if (format is not ("I4" or "I8"))
            return (cutout, graduated, false);

        return renderClass switch
        {
            // The runtime emits AA_ZB_TEX_TERR: texel alpha becomes coverage,
            // which glTF approximates with a depth-writing mask.
            N64TexFile.N64TextureRenderClass.TextureCoverage =>
                (cutout || graduated, false, false),
            // The runtime emits a FORCE_BL/ZMODE_XLU surface. Keep the actual
            // art profile, and carry blending independently so binary/solid
            // alpha cannot collapse this authored state to Mask/Opaque.
            N64TexFile.N64TextureRenderClass.Translucent => (cutout, graduated, true),
            // Opaque render state ignores physical texture alpha. Face and
            // vertex blend signals are applied separately by N64ModelWriter.
            N64TexFile.N64TextureRenderClass.Opaque => (false, false, false),
            // No standard low-bit class was available (image record or custom
            // threshold path), so retain the historical content policy.
            _ => (cutout, graduated, false)
        };
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
    internal static string? TryFindCarveRoot(AssetSource source)
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
