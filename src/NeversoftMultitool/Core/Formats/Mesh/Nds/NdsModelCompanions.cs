using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Resolves what a single DS model needs from its neighbours, given nothing but
///     an <see cref="AssetSource" /> — the shape a GUI row or the generic
///     <c>mesh</c> command works in, where there is one entry in hand rather than a
///     whole container to index.
///
///     The binding is the one the loader spells: a model set is keyed by <c>idA</c>,
///     its texture bank is <c>.\&lt;idA&gt;.textureinfo.bin</c>, and each bank record
///     names its pixels <c>.\&lt;pixelId&gt;.texture.bin</c>. So the companions are
///     fetched by name and nothing is inferred.
///
///     What this deliberately does NOT do is the GX-state join
///     (<see cref="NdsTextureBankResolver" />). That needs every bank in the
///     container at once, which an <see cref="AssetSource" /> cannot enumerate, and
///     it only ever spoke for models whose name was never recovered. The batch
///     <c>nds-mesh</c> path keeps it; a per-entry caller gets the stated binding or
///     no texture, which is the honest answer rather than a guess.
/// </summary>
public static class NdsModelCompanions
{
    /// <summary>
    ///     The model's texture bank plus a reader for its texel blobs, or null when
    ///     the entry has no recovered name, names no bank, or the bank does not
    ///     validate against the blobs it points at.
    /// </summary>
    public static NdsTextureSource? TryResolveTextures(AssetSource source, uint idA)
    {
        ArgumentNullException.ThrowIfNull(source);

        byte[]? bankData;
        try
        {
            bankData = source.TryReadCompanion(NdsModelSet.TextureBankName(idA)[2..]);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                   or EndOfStreamException or NotSupportedException)
        {
            return null;
        }

        if (bankData == null)
            return null;

        var texels = NdsTextureCompanions.BuildReader(source);
        return NdsTextureBank.TryParseValidated(
            bankData, NdsTextureCompanions.LengthOf(texels), out var bank)
            ? new NdsTextureSource(bank, texels)
            : null;
    }

    /// <summary>
    ///     The model-set ids an entry's own recovered name carries, or false when the
    ///     name was never recovered (the entry then extracts as <c>&lt;crc&gt;.bin</c>).
    /// </summary>
    public static bool TryReadSetIds(AssetSource source, out uint idA, out uint idB)
    {
        ArgumentNullException.ThrowIfNull(source);
        // The container resolves the name; a loose file keeps it as its filename.
        return NdsModelSet.TryParseGeometryName(NameOf(source), out idA, out idB);
    }

    /// <summary>
    ///     The model's clip library, in index order. Sk8land spells clips
    ///     <c>.\&lt;idA&gt;.&lt;idB&gt;.&lt;n&gt;.animation.bin</c> and the run is
    ///     CONTIGUOUS from 0, so enumeration is asking for the next one until it is
    ///     not there — no container index, and a hole cannot silently truncate a
    ///     library because there are none.
    ///
    ///     Downhill Jam and Proving Ground spell animation differently and are not
    ///     reached here; they return nothing rather than a wrong clip.
    /// </summary>
    public static IReadOnlyList<(int Index, NdsAnimationFile Clip)> ReadClips(
        AssetSource source, int limit = 512)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!TryReadSetIds(source, out var idA, out var idB))
            return [];

        var clips = new List<(int, NdsAnimationFile)>();
        for (var n = 0; n < limit; n++)
        {
            byte[]? data;
            try
            {
                data = source.TryReadCompanion(NdsModelSet.ClipName(idA, idB, n)[2..]);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                                       or EndOfStreamException or NotSupportedException)
            {
                break;
            }

            if (data == null)
                break;
            if (!NdsAnimationFile.TryParse(data, out var clip))
                break;
            clips.Add((n, clip));
        }

        return clips;
    }

    private static string NameOf(AssetSource source)
    {
        var name = source.EntryName;
        // A container entry's name has already had the loader's ".\" stripped, while
        // NdsModelSet.TryParseGeometryName wants the spelling the loader uses.
        return name.StartsWith(".\\", StringComparison.Ordinal) ? name : ".\\" + name;
    }
}
