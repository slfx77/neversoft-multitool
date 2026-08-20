using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.N64;

/// <summary>
///     Pins the boot image's load base, which every absolute pointer inside
///     <c>boot.bin</c> is resolved against.
/// </summary>
public sealed class N64BootImageTests(TestPaths paths)
{
    /// <summary>
    ///     The base is derived from the image (light-rig pointer minus the body
    ///     offset), so this asserts the derivation lands on the values two
    ///     unrelated measurements already reached: the prologue vote in
    ///     <c>tools/reverse-engineering/n64/n64_disasm.py</c>, and the
    ///     dispatch-table arithmetic pinned by the N64 audio tests
    ///     (<c>base + handlerOffset == dispatchEntry</c>). Graphics data,
    ///     instruction encodings, and audio code agreeing on one number is what
    ///     makes the base trustworthy rather than plausible.
    /// </summary>
    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64", 0x80016990u)]
    [InlineData("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
        "Tony Hawk's Pro Skater 2 (USA).z64", 0x80016B20u)]
    [InlineData("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
        "Tony Hawk's Pro Skater 3 (USA).z64", 0x80016B20u)]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)",
        "Spider-Man (USA).z64", 0x80016AE0u)]
    public void DerivedLoadBase_MatchesTheIndependentlyMeasuredValue(
        string buildName, string romName, uint expectedBase)
    {
        var boot = ReadBoot(buildName, romName);
        var image = N64BootImage.TryOpen(boot);

        Assert.NotNull(image);
        Assert.Equal(expectedBase, image!.LoadBase);

        // The first byte resolves to the base, and the last byte still resolves;
        // one past the end does not.
        Assert.True(image.TryGetOffset(expectedBase, out var first));
        Assert.Equal(0, first);
        Assert.True(image.TryGetOffset(expectedBase + (uint)image.Length - 1, out var last));
        Assert.Equal(image.Length - 1, last);
        Assert.False(image.TryGetOffset(expectedBase + (uint)image.Length, out _));
        Assert.False(image.TryGetOffset(expectedBase - 1, out _));
    }

    [CorpusTheory]
    [InlineData("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
        "Tony Hawk's Pro Skater (USA).z64")]
    [InlineData("Spider-Man (2000-11-21, N64 - Final)", "Spider-Man (USA).z64")]
    public void KsegAliases_ResolveToTheSameOffset(string buildName, string romName)
    {
        // The ports mix cached (0x80…) and uncached (0xA0…) pointers to the
        // same word; they differ only in bit 29 and must not resolve
        // differently.
        var image = N64BootImage.TryOpen(ReadBoot(buildName, romName));
        Assert.NotNull(image);

        var cached = image!.LoadBase | 0x1000u;
        var uncached = cached | 0x2000_0000u;
        Assert.True(image.TryGetOffset(cached, out var a));
        Assert.True(image.TryGetOffset(uncached, out var b));
        Assert.Equal(a, b);
    }

    [Fact]
    public void ImageWithoutTheLightRig_YieldsNoBaseRatherThanAGuess()
    {
        // Fail closed: without the setup display list there is nothing to
        // derive a base from, and a wrong base silently resolves every pointer
        // to the wrong place.
        Assert.Null(N64BootImage.TryOpen(new byte[0x4000]));
    }

    private byte[] ReadBoot(string buildName, string romName)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        Assert.True(N64AssetCarver.TryCarve(File.ReadAllBytes(romPath!), out var assets));
        return Assert.Single(assets, static a => a.Path == "boot.bin").Data;
    }
}
