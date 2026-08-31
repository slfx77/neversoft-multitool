using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Video;

namespace NeversoftMultitool.Tests.Core.Formats.Video;

/// <summary>
///     The next-gen compound video suffixes routed 2026-08-26. Every routing
///     check in the CLI, the probe and the Video tab previously used
///     <see cref="Path.GetExtension" />, which sees only ".xen" for
///     <c>foo.bik.xen</c> — so 1,062 files / 17.9 GiB of Bink across THAW,
///     Project 8 and Proving Ground were invisible to all three. The newly
///     routed classes are content-gated so that pointing the tool at unrelated
///     files stays a no-op instead of becoming a hard error.
/// </summary>
public sealed class FfmpegVideoFormatsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nmt-ffmpeg-video-" + Guid.NewGuid().ToString("N"));

    public FfmpegVideoFormatsTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
            // Best-effort cleanup.
        }
    }

    [Fact]
    public void IsFfmpegVideo_BikXen_RequiresBinkMagic()
    {
        var bink = Write("movie.bik.xen", Bink(64));
        var notBink = Write("other.bik.xen", new byte[64]);

        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(bink));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(notBink));
    }

    /// <summary>
    ///     PSMF's own size identity (headerSize + streamSize == file length) is
    ///     exact for 334/334 corpus files with zero false positives across
    ///     69,088 files in both Project 8 PSP builds, so it gates for free.
    /// </summary>
    [Fact]
    public void IsFfmpegVideo_Pmf_RequiresPsmfSizeIdentity()
    {
        var valid = Write("movie.pmf", Psmf(headerSize: 16, streamSize: 16, totalLength: 32));
        var wrongIdentity = Write("bad.pmf", Psmf(headerSize: 16, streamSize: 999, totalLength: 32));
        var notPsmf = Write("plain.pmf", new byte[32]);

        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(valid));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(wrongIdentity));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(notPsmf));
    }

    [Fact]
    public void IsFfmpegVideo_LegacySuffixes_StayUngated()
    {
        // .sfd/.pss/.bik routed before this change and did not content-check;
        // keeping them ungated means no existing behaviour shifts.
        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(Path.Combine("in", "a.sfd")));
        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(Path.Combine("in", "b.PSS")));
        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(Path.Combine("in", "c.BiK")));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(Path.Combine("in", "notes.txt")));
    }

    [Theory]
    [InlineData("credits.bik.xen", "credits")]
    [InlineData("CREDITS.BIK.XEN", "CREDITS")]
    [InlineData("credits.bik", "credits")]
    [InlineData("movie.pmf", "movie")]
    [InlineData("clip.sfd", "clip")]
    public void GetOutputStem_StripsTheWholeCompoundSuffix(string fileName, string expected)
    {
        Assert.Equal(expected, FfmpegVideoFormats.GetOutputStem(Path.Combine("in", fileName)));
    }

    /// <summary>
    ///     The duplicate-stem guard and the converter's output name must use the
    ///     same rule. Under the old <see cref="Path.GetFileNameWithoutExtension" />
    ///     rule "credits.bik" and "credits.bik.xen" produced two DIFFERENT keys
    ///     ("credits" and "credits.bik") so the guard stayed silent — while the
    ///     converter wrote both to one file. The corpus has no such directory
    ///     today, so this is a correctness requirement rather than a live bug.
    /// </summary>
    [Fact]
    public void FindDuplicateOutputStems_CatchesBikAndBikXenCollision()
    {
        var bik = Path.Combine("in", "credits.bik");
        var bikXen = Path.Combine("in", "credits.bik.xen");

        Assert.Equal(["credits"], SfdCommand.FindDuplicateOutputStems([bik, bikXen]));
    }

    [Fact]
    public void SelectCandidatePaths_PicksUpContentGatedNextGenFiles()
    {
        var bink = Write("movie.bik.xen", Bink(64));
        var junk = Write("junk.bik.xen", new byte[64]);
        var legacy = Path.Combine(_root, "legacy.bik");

        var selected = SfdCommand.SelectCandidatePaths([bink, junk, legacy]);

        Assert.Equal([bink, legacy], selected);
    }

    [Fact]
    public void IsAudioUndecodable_OnlyPmf()
    {
        // PSP PSMF audio is ATRAC3+ in a private stream that ffmpeg cannot
        // decode, so those convert video-only instead of failing the file.
        Assert.True(FfmpegVideoFormats.IsAudioUndecodable(Path.Combine("in", "a.pmf")));
        Assert.False(FfmpegVideoFormats.IsAudioUndecodable(Path.Combine("in", "a.bik.xen")));
    }

    private string Write(string name, byte[] content)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private static byte[] Bink(int length)
    {
        var data = new byte[length];
        "BIKi"u8.CopyTo(data);
        return data;
    }

    private static byte[] Psmf(uint headerSize, uint streamSize, int totalLength)
    {
        var data = new byte[totalLength];
        "PSMF"u8.CopyTo(data);
        "0014"u8.CopyTo(data.AsSpan(4));
        WriteBigEndian(data, 0x08, headerSize);
        WriteBigEndian(data, 0x0C, streamSize);
        return data;
    }

    private static void WriteBigEndian(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
