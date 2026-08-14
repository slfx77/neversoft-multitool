using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxCueSheetBatchExporterTests
{
    [Fact]
    public void TryExtractToWav_EmptySheetSet_SignalsRawFallback()
    {
        WithTempDirectory(tempDir =>
        {
            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                [],
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.False(handled);
            Assert.Null(result);
        });
    }

    [Fact]
    public void TryExtractToWav_NoTrueCueSheets_SignalsRawFallbackWithoutWriting()
    {
        WithTempDirectory(tempDir =>
        {
            var bank = CreateBank();
            SfxCueSheetBatchInput[] sheets =
            [
                new("malformed.sfx", "sounds/malformed.sfx", [0x12]),
                new(
                    "fallback.sfx",
                    "sounds/fallback.sfx",
                    SfxTestBuilder.CreateSfx([0], [1]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                bank,
                tempDir,
                out var result);

            Assert.False(handled);
            Assert.Null(result);
            Assert.Empty(Directory.EnumerateFiles(tempDir, "*.wav", SearchOption.AllDirectories));
        });
    }

    [Fact]
    public void TryExtractToWav_OneResolvedSheet_PreservesFlatBankCueLayout()
    {
        WithTempDirectory(tempDir =>
        {
            SfxCueSheetBatchInput[] sheets =
            [
                new("voice.sfx", "sounds/voice.sfx", SfxTestBuilder.CreateSfx([0]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.True(handled);
            var conversion = Assert.IsType<AudioConvertResult>(result);
            Assert.True(conversion.Success, conversion.ErrorMessage);
            Assert.Equal(1, conversion.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(tempDir, "bank", "000.wav")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "bank", "voice")));
        });
    }

    [Fact]
    public void TryExtractToWav_MultipleResolvedSheets_UsesPerSheetDirectoriesAndAggregatesWrites()
    {
        WithTempDirectory(tempDir =>
        {
            SfxCueSheetBatchInput[] sheets =
            [
                new("effects.sfx", "sounds/effects.sfx", SfxTestBuilder.CreateSfx([0, 1])),
                new("voices.sfx", "sounds/voices.sfx", SfxTestBuilder.CreateSfx([1]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.True(handled);
            var conversion = Assert.IsType<AudioConvertResult>(result);
            Assert.True(conversion.Success, conversion.ErrorMessage);
            Assert.Equal(3, conversion.SamplesWritten);
            var effectZero = Path.Combine(tempDir, "bank", "effects", "000.wav");
            var effectOne = Path.Combine(tempDir, "bank", "effects", "001.wav");
            var voiceZero = Path.Combine(tempDir, "bank", "voices", "000.wav");
            Assert.True(File.Exists(effectZero));
            Assert.True(File.Exists(effectOne));
            Assert.True(File.Exists(voiceZero));
            Assert.Equal(3, Directory.GetFiles(tempDir, "*.wav", SearchOption.AllDirectories).Length);
        });
    }

    [Fact]
    public void TryExtractToWav_CollidingSheetNames_GetDistinctStableSafeDirectories()
    {
        WithTempDirectory(tempDir =>
        {
            SfxCueSheetBatchInput[] sheets =
            [
                new("demo.sfx", "archive/A/demo.sfx", SfxTestBuilder.CreateSfx([0])),
                new("DEMO.SFX", "archive/a/DEMO.SFX", SfxTestBuilder.CreateSfx([1]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.True(handled);
            Assert.Equal(2, Assert.IsType<AudioConvertResult>(result).SamplesWritten);
            var sheetDirectories = Directory.GetDirectories(Path.Combine(tempDir, "bank"));
            Assert.Equal(2, sheetDirectories.Length);
            Assert.Equal(
                2,
                sheetDirectories
                    .Select(Path.GetFileName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .Count);
            Assert.All(sheetDirectories, directory =>
                Assert.True(File.Exists(Path.Combine(directory, "000.wav"))));
        });
    }

    [Fact]
    public void TryExtractToWav_ReversedSheets_ProduceTheSameRelativePathSet()
    {
        var sheets = new[]
        {
            new SfxCueSheetBatchInput(
                "demo.sfx",
                "archive/A/demo.sfx",
                SfxTestBuilder.CreateSfx([0])),
            new SfxCueSheetBatchInput(
                "DEMO.SFX",
                "archive/a/DEMO.SFX",
                SfxTestBuilder.CreateSfx([1]))
        };
        var forwardPaths = ExportAndCollectRelativePaths(sheets);
        var reversePaths = ExportAndCollectRelativePaths(sheets.Reverse().ToArray());

        Assert.Equal(forwardPaths, reversePaths);
    }

    [Fact]
    public void TryExtractToWav_MixedSheets_ExportsOnlyTrueSheetUsingSingleSheetLayout()
    {
        WithTempDirectory(tempDir =>
        {
            SfxCueSheetBatchInput[] sheets =
            [
                new("good.sfx", "sounds/good.sfx", SfxTestBuilder.CreateSfx([0])),
                new("malformed.sfx", "sounds/malformed.sfx", [0x34]),
                new(
                    "fallback.sfx",
                    "sounds/fallback.sfx",
                    SfxTestBuilder.CreateSfx([0], [1]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.True(handled);
            Assert.Equal(1, Assert.IsType<AudioConvertResult>(result).SamplesWritten);
            Assert.True(File.Exists(Path.Combine(tempDir, "bank", "000.wav")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "bank", "good")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "bank", "fallback")));
        });
    }

    [Fact]
    public void TryExtractToWav_MultipleSheetStems_AreTraversalAndDeviceNameSafe()
    {
        WithTempDirectory(tempDir =>
        {
            SfxCueSheetBatchInput[] sheets =
            [
                new("CON.sfx", "sounds/CON.sfx", SfxTestBuilder.CreateSfx([0])),
                new("../voice.sfx", "../outside/voice.sfx", SfxTestBuilder.CreateSfx([1]))
            ];

            var handled = SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result);

            Assert.True(handled);
            Assert.Equal(2, Assert.IsType<AudioConvertResult>(result).SamplesWritten);
            Assert.True(File.Exists(Path.Combine(tempDir, "bank", "_CON", "000.wav")));
            Assert.True(File.Exists(Path.Combine(tempDir, "bank", "voice", "000.wav")));
            Assert.False(Directory.Exists(Path.Combine(tempDir, "outside")));
        });
    }

    private static SfxExtractor.SfxBankBytes CreateBank()
    {
        return new SfxExtractor.SfxBankBytes(
            SfxTestBuilder.CreateKat([0x1000, 0x2000], [4, 4], 16000),
            "KAT");
    }

    private static void WithTempDirectory(Action<string> test)
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_sheet_batch");
        try
        {
            test(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    private static string[] ExportAndCollectRelativePaths(IReadOnlyList<SfxCueSheetBatchInput> sheets)
    {
        string[] paths = [];
        WithTempDirectory(tempDir =>
        {
            Assert.True(SfxCueSheetBatchExporter.TryExtractToWav(
                sheets,
                "bank",
                CreateBank(),
                tempDir,
                out var result));
            Assert.True(Assert.IsType<AudioConvertResult>(result).Success);
            paths = Directory.GetFiles(tempDir, "*.wav", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(tempDir, path).Replace('\\', '/'))
                .OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static path => path, StringComparer.Ordinal)
                .ToArray();
        });
        return paths;
    }
}
