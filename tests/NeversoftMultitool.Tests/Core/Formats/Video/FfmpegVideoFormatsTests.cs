using System.Buffers.Binary;
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
    private const string Thps4PcBuild = "Tony Hawk's Pro Skater 4 (2003-7-18, PC - Final)";

    private readonly TestPaths _paths;
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "nmt-ffmpeg-video-" + Guid.NewGuid().ToString("N"));

    public FfmpegVideoFormatsTests(TestPaths paths)
    {
        _paths = paths;
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

    [Fact]
    public void IsFfmpegVideo_Tgr_RequiresBinkMagic()
    {
        var bink = Write("movie.TgR", Bink(64));
        var notBink = Write("script.tgr", new byte[64]);
        var disguisedPsmf = Write(
            "psmf.tgr",
            Psmf(headerSize: 16, streamSize: 16, totalLength: 32));

        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(bink));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(notBink));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(disguisedPsmf));
    }

    [Fact]
    public void IsFfmpegVideo_Smo_RequiresExactThps4PcCarrierStructure()
    {
        var valid = Write("music.SmO", Smo());
        var genericBink = Write("movie.smo", Bink(128));
        var wrongRevision = Smo();
        wrongRevision[3] = (byte)'k';
        var wrongRevisionPath = Write("revision.smo", wrongRevision);
        var wrongSize = Smo();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongSize.AsSpan(4), 12);
        var wrongSizePath = Write("size.smo", wrongSize);
        var wrongShape = Smo();
        BinaryPrimitives.WriteUInt32LittleEndian(wrongShape.AsSpan(20), 640);
        var wrongShapePath = Write("shape.smo", wrongShape);

        Assert.True(FfmpegVideoFormats.IsFfmpegVideo(valid));
        Assert.True(FfmpegVideoFormats.IsThps4PcSmo(File.ReadAllBytes(valid)));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(genericBink));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(wrongRevisionPath));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(wrongSizePath));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(wrongShapePath));
    }

    [Fact]
    public void IsFfmpegVideo_PpvAndVlcRemainOutOfScopeEvenWithSmoPayload()
    {
        var ppv = Write("foreign.ppv", Smo());
        var vlc = Write("foreign.vlc", Smo());

        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(ppv));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(vlc));
    }

    [Theory]
    [InlineData("BIKb")]
    [InlineData("BIKf")]
    [InlineData("BIKg")]
    [InlineData("BIKh")]
    [InlineData("BIKi")]
    [InlineData("BIKk")]
    [InlineData("KB2a")]
    [InlineData("KB2d")]
    [InlineData("KB2f")]
    [InlineData("KB2g")]
    [InlineData("KB2h")]
    [InlineData("KB2i")]
    [InlineData("KB2j")]
    [InlineData("KB2k")]
    public void IsBink_AcceptsEveryFfmpegFourCcRevision(string fourCc)
    {
        Assert.True(FfmpegVideoFormats.IsBink(System.Text.Encoding.ASCII.GetBytes(fourCc)));
    }

    [Theory]
    [InlineData("BIKx")]
    [InlineData("BIK0")]
    [InlineData("KB2b")]
    [InlineData("KB2z")]
    [InlineData("biki")]
    public void IsBink_RejectsThreeBytePrefixWithUnknownRevision(string fourCc)
    {
        Assert.False(FfmpegVideoFormats.IsBink(System.Text.Encoding.ASCII.GetBytes(fourCc)));
    }

    [CorpusFact]
    public void Thps4Pc_AllTgrMoviesAreContentGatedBink()
    {
        var files = _paths.FindSampleFiles(Thps4PcBuild, "*.tgr").ToArray();
        Assert.Equal(27, files.Length);

        foreach (var file in files)
        {
            Assert.True(FfmpegVideoFormats.IsBink(file), Path.GetFileName(file));
            Assert.True(FfmpegVideoFormats.IsFfmpegVideo(file), Path.GetFileName(file));
        }
    }

    [CorpusFact]
    public void Thps4Pc_AllSmoSoundtracksUseStrictBikiCarrierRoute()
    {
        var files = _paths.FindSampleFiles(Thps4PcBuild, "*.smo").ToArray();
        Assert.Equal(47, files.Length);

        foreach (var file in files)
        {
            Assert.True(FfmpegVideoFormats.IsThps4PcSmo(file), Path.GetFileName(file));
            Assert.True(FfmpegVideoFormats.IsFfmpegVideo(file), Path.GetFileName(file));
        }
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
        Assert.True(FfmpegVideoFormats.IsPsmf(File.ReadAllBytes(valid)));
        Assert.False(FfmpegVideoFormats.IsFfmpegVideo(wrongIdentity));
        Assert.False(FfmpegVideoFormats.IsPsmf(File.ReadAllBytes(wrongIdentity)));
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
    [InlineData("data001.tgr", "data001")]
    [InlineData("ACDC.smo", "ACDC")]
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
        var tgr = Write("data001.tgr", Bink(64));
        var nonBinkTgr = Write("goals.tgr", new byte[64]);
        var smo = Write("music.smo", Smo());
        var nonBinkSmo = Write("mesh.smo", new byte[128]);
        var legacy = Path.Combine(_root, "legacy.bik");

        var selected = SfdCommand.SelectCandidatePaths(
            [bink, junk, tgr, nonBinkTgr, smo, nonBinkSmo, legacy]);

        Assert.Equal([bink, tgr, smo, legacy], selected);
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

    private static byte[] Smo()
    {
        const int length = 128;
        const uint frameCount = 4;
        var data = new byte[length];
        "BIKi"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), length - 8);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(16), frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(20), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(24), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(28), 15);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(32), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(44), 4096);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(48), 48_000);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(50), 0x7000);

        // 44-byte fixed header + 12 bytes of one-track metadata + five
        // frame-offset words. Low bit marks the first frame as a key frame.
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(56), 77);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(60), 88);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(64), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(68), 112);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(72), length);
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
