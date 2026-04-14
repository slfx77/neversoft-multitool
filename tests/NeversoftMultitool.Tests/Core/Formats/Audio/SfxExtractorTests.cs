using System.Buffers.Binary;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Tests.Core;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class SfxExtractorTests(TestPaths paths)
{
    private string SpiderManSampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Spider-Man (2001-2-14, DC - Prototype)",
            "DEM1.SFX");

    private string SpiderManVabOnlySampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Spider-Man (2001-2-14, DC - Prototype)",
            "L8A2.SFX");

    private string SpiderManAliasSampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Spider-Man (2001-2-14, DC - Prototype)",
            "LAA2.SFX");

    private string SpiderManPaddedSampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Spider-Man (2001-2-14, DC - Prototype)",
            "ZART.SFX");

    private string Thps2SampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)",
            "B1.SFX");

    private string Thps2HeaderVariantSampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Tony Hawk's Pro Skater 2 (2000-11-15, DC - Final)",
            "HEAVEN.SFX");

    private string SpiderManShellSampleFile =>
        Path.Combine(
            paths.SampleBuildsDir!,
            "Spider-Man (2001-2-14, DC - Prototype)",
            "SHELL.SFX");

    [Fact]
    public void CanExtract_DirectKatReference_ReturnsTrue()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_direct_kat");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            var katPath = Path.Combine(tempDir, "demo.kat");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1, 3], appendTerminator: true, trailingPaddingEntries: 2));
            File.WriteAllBytes(katPath, SfxTestBuilder.CreateKat([0x1000, 0x2000, 0x3000], [4, 4, 4], 16000));

            var success = SfxExtractor.CanExtract(sfxPath, out var error);

            Assert.True(success, error);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CanExtract_MissingCompanion_ReturnsFalse()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_missing_bank");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1, 2]));

            var success = SfxExtractor.CanExtract(sfxPath, out var error);

            Assert.False(success);
            Assert.Contains("soundbank", error, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void EnumerateSamples_DirectReferenceSubset_UsesPackedSampleNumbers()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_enumerate_subset");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            var katPath = Path.Combine(tempDir, "demo.kat");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1, 3]));
            File.WriteAllBytes(katPath, SfxTestBuilder.CreateKat([0x1000, 0x2000, 0x3000], [4, 4, 4], 22050));

            var samples = SfxExtractor.EnumerateSamples(sfxPath);

            Assert.Equal(2, samples.Count);
            Assert.Equal(0, samples[0].CueIndex);
            Assert.Equal(0, samples[0].BankSampleIndex);
            Assert.Equal(1, samples[1].CueIndex);
            Assert.Equal(2, samples[1].BankSampleIndex);
            Assert.Equal("KAT", samples[0].BankFormat);
            Assert.Equal("PCM 16-bit", samples[0].Encoding);
            Assert.Equal(22050, samples[0].SampleRate);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ExtractToWav_DirectReference_WritesCueNamedFiles()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_extract");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            var katPath = Path.Combine(tempDir, "demo.kat");
            var outputDir = Path.Combine(tempDir, "out");

            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1, 3]));
            File.WriteAllBytes(katPath, SfxTestBuilder.CreateKat([0x1000, 0x2000, 0x3000], [4, 4, 4], 16000));

            var result = SfxExtractor.ExtractToWav(sfxPath, outputDir);

            Assert.True(result.Success, result.ErrorMessage);
            Assert.Equal(2, result.SamplesWritten);
            Assert.True(File.Exists(Path.Combine(outputDir, "demo", "000.wav")));
            Assert.True(File.Exists(Path.Combine(outputDir, "demo", "001.wav")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ProbeAudio_SfxResolved_Supported()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_probe_supported");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            var katPath = Path.Combine(tempDir, "demo.kat");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1]));
            File.WriteAllBytes(katPath, SfxTestBuilder.CreateKat([0x1000], [4], 16000));

            var result = FormatProbe.ProbeAudio(sfxPath);

            Assert.Equal(FormatProbe.FormatSupport.Supported, result.Support);
            Assert.Equal("SFX Cue Bank", result.FormatName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ProbeAudio_SfxMissingCompanion_Unsupported()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_probe_unsupported");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1]));

            var result = FormatProbe.ProbeAudio(sfxPath);

            Assert.Equal(FormatProbe.FormatSupport.Unsupported, result.Support);
            Assert.Contains("soundbank", result.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void CanExtract_RepresentativeSpiderManSample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(SpiderManSampleFile), "Representative Spider-Man Dreamcast SFX sample not found");

        var success = SfxExtractor.CanExtract(SpiderManSampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeSpiderManVabOnlySample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(SpiderManVabOnlySampleFile), "Representative Spider-Man VAB-only SFX sample not found");

        var success = SfxExtractor.CanExtract(SpiderManVabOnlySampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeSpiderManAliasSample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(SpiderManAliasSampleFile), "Representative Spider-Man alias SFX sample not found");

        var success = SfxExtractor.CanExtract(SpiderManAliasSampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeSpiderManPaddedSample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(SpiderManPaddedSampleFile), "Representative Spider-Man padded SFX sample not found");

        var success = SfxExtractor.CanExtract(SpiderManPaddedSampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeThps2Sample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(Thps2SampleFile), "Representative THPS2 Dreamcast SFX sample not found");

        var success = SfxExtractor.CanExtract(Thps2SampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeThps2HeaderVariantSample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(Thps2HeaderVariantSampleFile), "Representative THPS2 header-variant SFX sample not found");

        var success = SfxExtractor.CanExtract(Thps2HeaderVariantSampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void CanExtract_RepresentativeSpiderManShellSample_Succeeds()
    {
        Assert.SkipWhen(!File.Exists(SpiderManShellSampleFile), "Representative Spider-Man shell SFX sample not found");

        var success = SfxExtractor.CanExtract(SpiderManShellSampleFile, out var error);

        Assert.True(success, error);
    }

    [Fact]
    public void EnumerateSamples_RepresentativeThps2Sample_UsesCompanionBankFallback()
    {
        Assert.SkipWhen(!File.Exists(Thps2SampleFile), "Representative THPS2 Dreamcast SFX sample not found");

        var samples = SfxExtractor.EnumerateSamples(Thps2SampleFile);
        var katPath = Path.ChangeExtension(Thps2SampleFile, ".KAT");
        var katSamples = KatExtractor.EnumerateSamples(katPath);

        Assert.Equal(katSamples.Count, samples.Count);
        Assert.All(samples, sample => Assert.Equal("KAT", sample.BankFormat));
    }

    [Fact]
    public void EnumerateSamples_VabBackedCue_UsesDefaultVabSampleRate()
    {
        var tempDir = FormatProbeTestHelper.CreateTempDirectory("sfx_vab_rate");
        try
        {
            var sfxPath = Path.Combine(tempDir, "demo.sfx");
            var vabPath = Path.Combine(tempDir, "demo.vab");
            File.WriteAllBytes(sfxPath, SfxTestBuilder.CreateSfx([1]));
            File.WriteAllBytes(vabPath, SfxTestBuilder.CreateVab([16]));

            var samples = SfxExtractor.EnumerateSamples(sfxPath);

            Assert.Single(samples);
            Assert.Equal("VAB", samples[0].BankFormat);
            Assert.Equal(44100, samples[0].SampleRate);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

internal static class SfxTestBuilder
{
    public static byte[] CreateSfx(
        IReadOnlyList<int> packedSampleNumbers,
        IReadOnlyList<int>? packedVariants = null,
        bool appendTerminator = false,
        int trailingPaddingEntries = 0)
    {
        var entryCount = packedSampleNumbers.Count + (appendTerminator ? 1 : 0) + trailingPaddingEntries;
        var data = new byte[4 + (entryCount * 16)];
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(0, 4), 0x0000003C);

        for (var i = 0; i < packedSampleNumbers.Count; i++)
        {
            var offset = 4 + (i * 16);
            var packedVariant = packedVariants != null && i < packedVariants.Count ? packedVariants[i] : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), 0x10001000u);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 4, 4), (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 8, 4), 0u);

            var packedId =
                ((uint)0x3C << 24) |
                ((uint)packedVariant << 16) |
                ((uint)packedSampleNumbers[i] << 8);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 12, 4), packedId);
        }

        if (appendTerminator)
        {
            var offset = 4 + (packedSampleNumbers.Count * 16);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset + 12, 4), 0xFFFFFFFF);
        }

        return data;
    }

    public static byte[] CreateKat(int[] offsets, int[] sizes, uint sampleRate)
    {
        const int entrySize = 44;
        var entryCount = offsets.Length;
        var headerSize = 4 + (entryCount * entrySize);
        var dataSize = sizes.Sum();
        var data = new byte[headerSize + dataSize];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0, 4), (uint)entryCount);

        var writeOffset = headerSize;
        for (var i = 0; i < entryCount; i++)
        {
            var entryOffset = 4 + (i * entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset, 4), 1);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 4, 4), (uint)writeOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 8, 4), (uint)sizes[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 12, 4), sampleRate);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 16, 4), 0);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 20, 4), 16);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 24, 4), 0);

            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(writeOffset, 2), (short)offsets[i]);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(writeOffset + 2, 2), (short)-offsets[i]);
            writeOffset += sizes[i];
        }

        return data;
    }

    public static byte[] CreateVab(int[] sampleSizes)
    {
        const int headerSize = 0x20;
        const int programTableSize = 128 * 16;
        const int toneTableSize = 16 * 32;
        const int sizeTableEntries = 256;

        var sizeTableSize = sizeTableEntries * sizeof(ushort);
        var sampleDataSize = sampleSizes.Sum();
        var totalSize = headerSize + programTableSize + toneTableSize + sizeTableSize + sampleDataSize;
        var data = new byte[totalSize];

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x00, 4), 0x56414270); // "pBAV"
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x04, 4), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(0x0C, 4), (uint)totalSize);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x10, 2), 0xEEEE);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x12, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x14, 2), (ushort)sampleSizes.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0x16, 2), (ushort)sampleSizes.Length);

        var programOffset = headerSize;
        data[programOffset] = (byte)sampleSizes.Length;

        var toneOffset = headerSize + programTableSize;
        for (var i = 0; i < sampleSizes.Length; i++)
        {
            var entryOffset = toneOffset + (i * 32);
            data[entryOffset + 4] = 60; // centre note
            data[entryOffset + 6] = 0;
            data[entryOffset + 7] = 127;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryOffset + 20, 2), 0);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryOffset + 22, 2), (ushort)(i + 1));
        }

        var sizeTableOffset = headerSize + programTableSize + toneTableSize;
        for (var i = 0; i < sampleSizes.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(
                data.AsSpan(sizeTableOffset + ((i + 1) * sizeof(ushort)), sizeof(ushort)),
                checked((ushort)(sampleSizes[i] / 8)));
        }

        return data;
    }
}
