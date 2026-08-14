using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxResolutionTests
{
    [Fact]
    public void TryResolveSamples_VabToneMapping_ClassifiesResolvedCueAndPreservesBothIndices()
    {
        var sfx = SfxTestBuilder.CreateSfx([0], [1]);
        var bank = new SfxExtractor.SfxBankBytes(
            SfxTestBuilder.CreateVab([16, 16]),
            "VAB");

        var success = SfxExtractor.TryResolveSamples(sfx, bank, out var resolution, out var error);

        Assert.True(success, error);
        var resolved = Assert.IsType<SfxResolution>(resolution);
        Assert.Equal(SfxResolutionKind.ResolvedCues, resolved.Kind);
        var sample = Assert.Single(resolved.Samples);
        Assert.Equal(0, sample.CueIndex);
        Assert.Equal(2, sample.BankSampleIndex);
        Assert.Equal(resolved.Samples, SfxExtractor.EnumerateSamples(sfx, bank));
    }

    [Fact]
    public void TryResolveSamples_KatCategoryNeedsMissingToneTable_ClassifiesFullBankFallback()
    {
        var sfx = SfxTestBuilder.CreateSfx([0], [1]);
        var bank = new SfxExtractor.SfxBankBytes(
            SfxTestBuilder.CreateKat([0x1000, 0x2000], [4, 4], 16000),
            "KAT");

        var success = SfxExtractor.TryResolveSamples(sfx, bank, out var resolution, out var error);

        Assert.True(success, error);
        var resolved = Assert.IsType<SfxResolution>(resolution);
        Assert.Equal(SfxResolutionKind.FullBankFallback, resolved.Kind);
        Assert.Equal([0, 1], resolved.Samples.Select(static sample => sample.BankSampleIndex));
        // Preserve the legacy EnumerateSamples shape while the result-level kind
        // tells new callers that these values are not authored cue mappings.
        Assert.Equal([0, 1], resolved.Samples.Select(static sample => sample.CueIndex));
        Assert.Equal(resolved.Samples, SfxExtractor.EnumerateSamples(sfx, bank));
    }

    [Fact]
    public void TryResolveSamples_MalformedCueTable_ReturnsFalseWithParserError()
    {
        var sfx = SfxTestBuilder.CreateSfx([0], appendTerminator: false);
        var bank = new SfxExtractor.SfxBankBytes(
            SfxTestBuilder.CreateKat([0x1000], [4], 16000),
            "KAT");

        var success = SfxExtractor.TryResolveSamples(sfx, bank, out var resolution, out var error);

        Assert.False(success);
        Assert.Null(resolution);
        Assert.Contains("terminator", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TryResolveSamples_EmptyCompanionBank_ReturnsFalseWithoutAResolution()
    {
        var sfx = SfxTestBuilder.CreateSfx([0]);
        var bank = new SfxExtractor.SfxBankBytes(
            SfxTestBuilder.CreateKat([], [], 16000),
            "KAT");

        var success = SfxExtractor.TryResolveSamples(sfx, bank, out var resolution, out var error);

        Assert.False(success);
        Assert.Null(resolution);
        Assert.Contains("could not be parsed", error, StringComparison.OrdinalIgnoreCase);
    }
}
