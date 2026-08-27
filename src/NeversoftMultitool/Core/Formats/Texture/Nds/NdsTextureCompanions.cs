using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool.Core.Formats.Texture.Nds;

/// <summary>
///     Joins a DS texture bank to the texel blobs it names.
///
///     A DS texture is two container files: the bank carries the GX parameters and
///     palettes, and each of its records names a separate pixel blob by id. The
///     loader spells that blob <c>.\%08x.texture.bin</c>, so the join needs no
///     content matching — but it does need the CONTAINER, because a bank on its own
///     cannot be told from a look-alike: <see cref="NdsTextureBank.TryParse" /> is a
///     size identity rather than a magic, and admits three Sk8land false positives
///     until the texel blobs are checked to exist at exactly the declared length.
///
///     Reads are cached because both halves of that check want the same bytes: first
///     the length, to validate the bank, then the blob itself, to decode.
/// </summary>
public static class NdsTextureCompanions
{
    /// <summary>The container name of the texel blob a bank record points at.</summary>
    public static string TexelName(uint pixelId) => $"{pixelId:x8}.texture.bin";

    /// <summary>
    ///     A cached reader for the texel blobs beside <paramref name="source" />.
    ///     Returns null for an id the container does not hold, and caches that too.
    /// </summary>
    public static Func<uint, byte[]?> BuildReader(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var cache = new Dictionary<uint, byte[]?>();
        return id =>
        {
            if (cache.TryGetValue(id, out var cached))
                return cached;
            byte[]? bytes;
            try
            {
                bytes = source.TryReadCompanion(TexelName(id));
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException
                                       or EndOfStreamException or NotSupportedException)
            {
                bytes = null;
            }

            cache[id] = bytes;
            return bytes;
        };
    }

    /// <summary>The length probe <see cref="NdsTextureBank.TryParseValidated" /> takes.</summary>
    public static Func<uint, long?> LengthOf(Func<uint, byte[]?> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return id => reader(id)?.LongLength;
    }

    /// <summary>
    ///     Parses <paramref name="data" /> as a bank, validated against the texel blobs
    ///     the source can reach. Returns false for anything that is not a bank, and
    ///     hands back the reader so a caller that then decodes does not read twice.
    /// </summary>
    public static bool TryParseBank(
        AssetSource source, byte[] data,
        out IReadOnlyList<NdsTextureEntry> textures, out Func<uint, byte[]?> texels)
    {
        texels = BuildReader(source);
        var reader = texels;
        // Cheap shape check first: the tab probes thousands of container entries and
        // most are nothing like a bank.
        if (data.Length < 36 || !NdsTextureBank.TryParseValidated(data, LengthOf(reader), out textures!))
        {
            textures = [];
            return false;
        }

        return true;
    }
}
