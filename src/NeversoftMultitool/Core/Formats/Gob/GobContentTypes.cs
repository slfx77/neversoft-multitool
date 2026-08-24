namespace NeversoftMultitool.Core.Formats.Gob;

/// <summary>
///     Gives an unnamed GOB file a real extension from its content.
///
///     Most of the container is machine-named (see <c>docs/formats/ds-gob-gfc.md</c>
///     for why those names cannot be recovered), so an unnamed file would otherwise
///     extract as an opaque <c>&lt;crc32&gt;.bin</c>. The 1,724 files whose real
///     names ARE proven give a Rosetta to write these rules against — each pairs a
///     true extension with real bytes — and every rule here is scored on it:
///     <b>0 of 2,351 named files are mislabelled</b>, which is the property that
///     matters, since a confidently wrong extension is worse than none.
///
///     Coverage is deliberately partial. The Vicarious Visions asset formats that
///     make up the bulk (a two-table sub-record container, ~46% of the corpus, and
///     a signed-coordinate family, ~21%) are not yet identified, and files matching
///     no rule keep <c>.bin</c> rather than being guessed at.
/// </summary>
public static class GobContentTypes
{
    /// <summary>
    ///     Four-byte magics, big-endian-packed. The Nitro SDK entries are the one
    ///     standard family these carts kept; the rest were each learned from a file
    ///     whose real name proves its extension.
    /// </summary>
    private static readonly Dictionary<uint, string> Magics = new()
    {
        [Pack("SWAV"u8)] = ".swav",
        [Pack("STRM"u8)] = ".strm",
        [Pack("SWAR"u8)] = ".swar",
        [Pack("SBNK"u8)] = ".sbnk",
        [Pack("SSEQ"u8)] = ".sseq",
        [Pack("SDAT"u8)] = ".sdat",
        [Pack("sawh"u8)] = ".hwas",  // streamed audio
        [Pack("PFPF"u8)] = ".prp",   // props
        [Pack("pmoc"u8)] = ".comp",  // 'comp' sub-record container
        [0x20004B00u] = ".sac"
    };

    /// <summary>Extension (with dot) for a file's content, or null when unrecognized.</summary>
    public static string? Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return null;

        if (Magics.TryGetValue(Pack(data), out var extension))
            return extension;
        if (data[..3].SequenceEqual("LWC"u8))
            return ".lwc";
        if (data[0] == (byte)'<' && LooksLikeXml(data))
            return ".xml";
        return null;
    }

    private static uint Pack(ReadOnlySpan<byte> head)
    {
        return ((uint)head[0] << 24) | ((uint)head[1] << 16) | ((uint)head[2] << 8) | head[3];
    }

    // WITHDRAWN: a ".pal" rule keyed on "exactly 512 bytes of u16s with bit 15
    // clear". Every proven palette does match it, but so does a 32x32 4bpp texel
    // blob whose indices happen to stay low — and once the texture banks named
    // their texel files, the Rosetta caught it mislabelling 13 of them. The two
    // are not separable from content alone at that size, and a confidently wrong
    // extension is worse than none, so palettes are only named when the container
    // names them.

    private static bool LooksLikeXml(ReadOnlySpan<byte> data)
    {
        var window = data[..Math.Min(256, data.Length)];
        foreach (var b in window)
        {
            if (b == (byte)'>')
                return true;
            if (b != (byte)'\t' && b != (byte)'\n' && b != (byte)'\r' && b is < 0x20 or > 0x7E)
                return false;
        }

        return false;
    }
}
