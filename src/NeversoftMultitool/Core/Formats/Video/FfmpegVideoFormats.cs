namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Single owner of the "hand it to ffmpeg" video suffixes, so the CLI, the
///     format probe and the Video tab cannot drift apart (they previously each
///     kept their own list, and every one of them used
///     <see cref="Path.GetExtension(string)" />, which sees only the LAST extension —
///     so the next-gen compound form <c>foo.bik.xen</c> resolved to
///     <c>.xen</c> and was invisible to all three).
///     Suffixes added 2026-08-26 after a corpus census:
///     <list type="bullet">
///         <item>
///             <c>.bik.xen</c> — 1,062 files / 17.9 GiB of Bink across THAW,
///             Project 8 and Proving Ground. Despite the Xenon suffix the PS3
///             builds ship the same files under the same name; the payload is
///             ordinary Bink and ffmpeg already decodes all 1,062.
///         </item>
///         <item>
///             <c>.pmf</c> — 334 files / 1.2 GiB of PSP PSMF (MPEG-PS with
///             H.264 video), the same container family as the supported
///             <c>.pss</c>.
///         </item>
///     </list>
///     The newly routed classes are CONTENT-GATED: a suffix alone would turn a
///     silent no-op into a hard error for anyone pointing the tool at unrelated
///     files. The pre-existing <c>.sfd</c>/<c>.pss</c>/<c>.bik</c> routing is
///     deliberately left ungated so its behaviour does not change.
/// </summary>
public static class FfmpegVideoFormats
{
    /// <summary>Every suffix the ffmpeg passthrough path accepts.</summary>
    public static readonly string[] Suffixes = [".sfd", ".pss", ".bik", ".bik.xen", ".pmf"];

    /// <summary>The suffixes whose routing is new, and so must prove itself on content.</summary>
    private static readonly string[] ContentGatedSuffixes = [".bik.xen", ".pmf"];

    /// <summary>Matches the suffix only — used where a cheap name test is wanted.</summary>
    public static bool HasVideoSuffix(string path)
    {
        return OrdinalFileName.HasAnySuffix(Path.GetFileName(path), Suffixes);
    }

    /// <summary>
    ///     True when the file should be handed to ffmpeg: a supported suffix,
    ///     plus a magic check for the classes routed since 2026-08-26.
    /// </summary>
    public static bool IsFfmpegVideo(string path)
    {
        var name = Path.GetFileName(path);
        if (!OrdinalFileName.HasAnySuffix(name, Suffixes))
            return false;

        if (!OrdinalFileName.HasAnySuffix(name, ContentGatedSuffixes))
            return true;

        return IsBink(path) || IsPsmf(path);
    }

    /// <summary>
    ///     Output stem for a converted file. Compound suffixes must be stripped
    ///     whole: <see cref="Path.GetFileNameWithoutExtension(string)" /> turns
    ///     <c>credits.bik.xen</c> into <c>credits.bik</c>, which both names the
    ///     output <c>credits.bik.mp4</c> and lets a sibling <c>credits.bik</c>
    ///     overwrite it without tripping the duplicate-stem guard.
    /// </summary>
    public static string GetOutputStem(string path)
    {
        return OrdinalFileName.StripCompoundSuffix(Path.GetFileName(path), Suffixes);
    }

    /// <summary>Bink 1 ("BIK" + version letter) or Bink 2 ("KB2" + letter).</summary>
    public static bool IsBink(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, 4, out var header, out var bytesRead) || bytesRead < 4)
            return false;

        return (header[0] == 'B' && header[1] == 'I' && header[2] == 'K')
               || (header[0] == 'K' && header[1] == 'B' && header[2] == '2');
    }

    /// <summary>
    ///     PSP PSMF: the "PSMF" magic plus the container's own size identity —
    ///     big-endian header size at +0x08 plus stream size at +0x0C equals the
    ///     file length. Measured exact for 334/334 corpus PMFs with zero false
    ///     positives across 69,088 files in both Project 8 PSP builds, so the
    ///     identity is a free exact-consume gate rather than a magic guess.
    /// </summary>
    public static bool IsPsmf(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, 16, out var header, out var bytesRead) || bytesRead < 16)
            return false;

        if (header[0] != 'P' || header[1] != 'S' || header[2] != 'M' || header[3] != 'F')
            return false;

        var headerSize = ReadBigEndianUInt32(header, 0x08);
        var streamSize = ReadBigEndianUInt32(header, 0x0C);

        try
        {
            return headerSize + (long)streamSize == new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    ///     True for PSP PSMF, whose ATRAC3+ audio ffmpeg cannot decode — those
    ///     convert video-only rather than failing the whole file.
    /// </summary>
    public static bool IsAudioUndecodable(string path)
    {
        return OrdinalFileName.HasSuffix(Path.GetFileName(path), ".pmf");
    }

    private static uint ReadBigEndianUInt32(byte[] data, int offset)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16)
                                           | (data[offset + 2] << 8) | data[offset + 3]);
    }
}
