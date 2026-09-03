using System.Buffers.Binary;

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
///             H.264 video and, where present, private-stream ATRAC3+), the
///             same container family as the supported <c>.pss</c>.
///         </item>
///         <item>
///             <c>.tgr</c> — THPS4 PC's 27 CD2 movies. The extension is also
///             used by unrelated Neversoft data, so only Bink payloads are
///             admitted.
///         </item>
///         <item>
///             <c>.smo</c> — THPS4 PC's 47 soundtrack carriers: BIKi with a
///             4x4 placeholder video stream and one stereo Bink-audio stream.
///             The route is restricted to that exact structural profile.
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
    public static readonly string[] Suffixes = [".sfd", ".pss", ".bik", ".bik.xen", ".pmf", ".tgr", ".smo"];

    /// <summary>The suffixes whose routing is new, and so must prove itself on content.</summary>
    private static readonly string[] ContentGatedSuffixes = [".bik.xen", ".pmf", ".tgr", ".smo"];

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

        if (OrdinalFileName.HasSuffix(name, ".pmf"))
            return IsPsmf(path);

        return OrdinalFileName.HasSuffix(name, ".smo")
            ? IsThps4PcSmo(path)
            : IsBink(path);
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

    /// <summary>
    ///     Bink 1 or Bink 2 FourCC recognized by FFmpeg's demuxer. Checking the
    ///     revision byte matters for overloaded extensions such as TGR: a bare
    ///     three-byte <c>BIK</c>/<c>KB2</c> prefix is not a Bink signature.
    /// </summary>
    public static bool IsBink(string path)
    {
        if (!BinaryProbeReader.TryReadHeader(path, 4, out var header, out var bytesRead) || bytesRead < 4)
            return false;

        return IsBink(header);
    }

    /// <summary>Byte-backed Bink check for archive entries that have no filesystem path.</summary>
    public static bool IsBink(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4)
            return false;

        var revision = data[3];
        return data[0] == 'B' && data[1] == 'I' && data[2] == 'K'
            ? revision is (byte)'b' or (byte)'f' or (byte)'g' or (byte)'h' or (byte)'i' or (byte)'k'
            : data[0] == 'K' && data[1] == 'B' && data[2] == '2'
              && revision is (byte)'a' or (byte)'d' or (byte)'f' or (byte)'g' or (byte)'h'
                  or (byte)'i' or (byte)'j' or (byte)'k';
    }

    /// <summary>
    ///     Strict THPS4-PC SMO gate. All 47 corpus files have this exact BIKi
    ///     soundtrack-carrier profile: the Bink length identity consumes the
    ///     file, the repeated frame count agrees, the frame-index table starts
    ///     at its computed end, video is a 4x4 15-fps placeholder, and there is
    ///     one stereo DCT-audio track at 44.1 or 48 kHz. This deliberately does
    ///     not turn arbitrary files with an overloaded <c>.smo</c> suffix into
    ///     ffmpeg inputs.
    /// </summary>
    public static bool IsThps4PcSmo(string path)
    {
        const int requiredHeaderSize = 60;
        if (!BinaryProbeReader.TryReadHeader(path, requiredHeaderSize, out var header, out var bytesRead)
            || bytesRead < requiredHeaderSize)
            return false;

        try
        {
            return IsThps4PcSmoHeader(header, new FileInfo(path).Length);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Byte-backed SMO gate for archive entries.</summary>
    public static bool IsThps4PcSmo(ReadOnlySpan<byte> data)
    {
        return IsThps4PcSmoHeader(data, data.Length);
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

        try
        {
            return IsPsmfHeader(header, new FileInfo(path).Length);
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>Byte-backed PSMF gate for archive entries.</summary>
    public static bool IsPsmf(ReadOnlySpan<byte> data)
    {
        return IsPsmfHeader(data, data.Length);
    }

    private static bool IsPsmfHeader(ReadOnlySpan<byte> data, long fileLength)
    {
        if (data.Length < 16 || !data[..4].SequenceEqual("PSMF"u8))
            return false;

        var headerSize = BinaryPrimitives.ReadUInt32BigEndian(data[8..]);
        var streamSize = BinaryPrimitives.ReadUInt32BigEndian(data[12..]);
        return headerSize >= 16
               && streamSize > 0
               && headerSize + (long)streamSize == fileLength;
    }

    private static bool IsThps4PcSmoHeader(ReadOnlySpan<byte> data, long fileLength)
    {
        const int fixedHeaderSize = 44;
        const int oneTrackMetadataSize = 12;
        const int firstFrameOffsetPosition = fixedHeaderSize + oneTrackMetadataSize;
        if (data.Length < firstFrameOffsetPosition + sizeof(uint) || fileLength < 0)
            return false;

        if (!data[..4].SequenceEqual("BIKi"u8))
            return false;

        var declaredPayloadSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if ((long)declaredPayloadSize + 8 != fileLength)
            return false;

        var frameCount = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        var largestFrameSize = BinaryPrimitives.ReadUInt32LittleEndian(data[12..]);
        if (frameCount == 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[16..]) != frameCount
            || largestFrameSize == 0
            || largestFrameSize > fileLength)
            return false;

        if (BinaryPrimitives.ReadUInt32LittleEndian(data[20..]) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(data[24..]) != 4
            || BinaryPrimitives.ReadUInt32LittleEndian(data[28..]) != 15
            || BinaryPrimitives.ReadUInt32LittleEndian(data[32..]) != 1
            || BinaryPrimitives.ReadUInt32LittleEndian(data[36..]) != 0
            || BinaryPrimitives.ReadUInt32LittleEndian(data[40..]) != 1)
            return false;

        var maximumDecodedAudioSize = BinaryPrimitives.ReadUInt32LittleEndian(data[44..]);
        var sampleRate = BinaryPrimitives.ReadUInt16LittleEndian(data[48..]);
        var audioFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[50..]);
        var trackId = BinaryPrimitives.ReadUInt32LittleEndian(data[52..]);
        if (maximumDecodedAudioSize == 0
            || sampleRate is not 44_100 and not 48_000
            || audioFlags != 0x7000
            || trackId != 0)
            return false;

        var frameIndexEnd = fixedHeaderSize
                            + oneTrackMetadataSize
                            + ((long)frameCount + 1) * sizeof(uint);
        if (frameIndexEnd >= fileLength)
            return false;

        var firstFrameOffset = BinaryPrimitives.ReadUInt32LittleEndian(data[firstFrameOffsetPosition..]);
        return (firstFrameOffset & ~1U) == frameIndexEnd;
    }
}
