using System.Buffers.Binary;
using System.Text;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Video;
using QbKeyLookup = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class ThawXmaBankTests(TestPaths paths)
{
    private const string ThawXenBuild =
        "Tony Hawk's American Wasteland (2005-10-29, X360 - Final)";
    private const uint KnownHash = 0xB90A3A81;
    private const string KnownName = "Anl_MBF_PitBull.png";

    [Fact]
    public void Probe_ConsumesHashSortedIndexAndOffsetPermutation()
    {
        var pair = BuildPair();

        var bank = ThawXmaBank.Probe(pair.Wad, pair.Index);

        Assert.NotNull(bank);
        Assert.Equal(44, bank.IndexSize);
        Assert.Equal(4096, bank.DataSize);
        Assert.Collection(bank.Samples,
            known =>
            {
                Assert.Equal(0, known.Index);
                Assert.Equal(KnownHash, known.NameHash);
                Assert.Equal(KnownName, known.Name);
                Assert.True(known.HasResolvedName);
                Assert.Equal(2048, known.DataOffset);
                Assert.Equal(2048, known.CompressedSize);
                Assert.Equal(22_050, known.SampleRate);
                Assert.Equal(1, known.Channels);
                Assert.Equal(0, known.Flags);
            },
            unknown =>
            {
                Assert.Equal(1, unknown.Index);
                Assert.Equal(pair.UnknownHash, unknown.NameHash);
                Assert.Equal($"0x{pair.UnknownHash:X8}", unknown.Name);
                Assert.False(unknown.HasResolvedName);
                Assert.Equal(0, unknown.DataOffset);
            });
    }

    [Fact]
    public void PairedName_IsRoutedThroughCliAndSharedProbe()
    {
        using var temp = new TempDirectory();
        var pair = BuildPair();
        var wadPath = Path.Combine(temp.Path, "music_xma.wad");
        var indexPath = Path.Combine(temp.Path, "music_xma.dat");
        File.WriteAllBytes(wadPath, pair.Wad);
        File.WriteAllBytes(indexPath, pair.Index);

        Assert.True(ThawXmaBank.HasSupportedFileName(wadPath));
        Assert.False(ThawXmaBank.HasSupportedFileName(indexPath));
        Assert.Equal([wadPath], AudioCommand.SelectNamedCandidatePaths([wadPath, indexPath]));

        var probe = FormatProbe.ProbeAudio(wadPath);
        Assert.Equal(FormatProbe.FormatSupport.Supported, probe.Support);
        Assert.Equal("THAW Xbox 360 XMA Sound Bank", probe.FormatName);
    }

    [Fact]
    public void SharedProbe_RejectsMissingOrInexactCompanion()
    {
        using var temp = new TempDirectory();
        var pair = BuildPair();
        var wadPath = Path.Combine(temp.Path, "xma.wad");
        File.WriteAllBytes(wadPath, pair.Wad);

        var missing = FormatProbe.ProbeAudio(wadPath);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, missing.Support);

        File.WriteAllBytes(Path.Combine(temp.Path, "xma.dat"), [.. pair.Index, 0]);
        var inexact = FormatProbe.ProbeAudio(wadPath);
        Assert.Equal(FormatProbe.FormatSupport.Unsupported, inexact.Support);
        Assert.Contains("exact", inexact.UnsupportedReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    public void Probe_RejectsBrokenIndependentIdentities(int mutation)
    {
        var pair = BuildPair();
        var index = pair.Index;
        var wad = pair.Wad;
        switch (mutation)
        {
            case 1:
                Array.Resize(ref index, index.Length + 1); // DAT trailing data.
                break;
            case 2:
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(24, 4), KnownHash); // Non-unique hash.
                break;
            case 3:
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(8, 4), 0); // Duplicate range/gap.
                break;
            case 4:
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(12, 4), 2047); // Unaligned size.
                break;
            case 5:
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(16, 4), 44_100); // Unknown dialect.
                break;
            case 6:
                BinaryPrimitives.WriteUInt16BigEndian(index.AsSpan(20, 2), 0x0100); // Effect-only flags.
                break;
            case 7:
                Array.Resize(ref wad, wad.Length + 2048); // Ranges do not reach EOF.
                break;
            case 8:
                wad[0] = 0; // Raw-XMA packet marker.
                break;
            case 9:
                BinaryPrimitives.WriteUInt32BigEndian(index.AsSpan(36, 4), 48_000);
                BinaryPrimitives.WriteUInt16BigEndian(index.AsSpan(42, 2), 2); // Mixed bank dialect.
                break;
        }

        Assert.Null(ThawXmaBank.Probe(wad, index));
    }

    [Fact]
    public void CreatePlayableStream_WrapsSelectedRangeAsCanonicalXmaRiff()
    {
        var pair = BuildPair();

        var stream = ThawXmaBank.CreatePlayableStream(pair.Wad, pair.Index, 0);

        Assert.Equal(2128, stream.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(stream, 0, 4));
        Assert.Equal(2120U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(4, 4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(stream, 8, 8));
        Assert.Equal(52U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(16, 4)));
        Assert.Equal(0x0166, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(20, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16LittleEndian(stream.AsSpan(22, 2)));
        Assert.Equal(22_050U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(24, 4)));
        Assert.Equal(4096U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(44, 4)));
        Assert.Equal(0x8000U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(48, 4)));
        Assert.Equal(4096U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(56, 4)));
        Assert.Equal("data", Encoding.ASCII.GetString(stream, 72, 4));
        Assert.Equal(2048U, BinaryPrimitives.ReadUInt32LittleEndian(stream.AsSpan(76, 4)));
        Assert.Equal(pair.Wad.AsSpan(2048, 2048).ToArray(), stream.AsSpan(80).ToArray());
        Assert.Empty(ThawXmaBank.CreatePlayableStream(pair.Wad, pair.Index, 2));
    }

    [Fact]
    public void ExtractEncoded_UsesResolvedAndHashFallbackNamesWithoutCollisions()
    {
        using var temp = new TempDirectory();
        var pair = BuildPair();

        var result = ThawXmaBank.ExtractEncoded(pair.Wad, pair.Index, "bank", temp.Path);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.SamplesWritten);
        var bankDirectory = Path.Combine(temp.Path, "bank");
        var known = Path.Combine(bankDirectory, "0000_Anl_MBF_PitBull.xma");
        var unknown = Path.Combine(bankDirectory, $"0001_0x{pair.UnknownHash:X8}.xma");
        Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(known), 0, 4));
        Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(unknown), 0, 4));
        Assert.Equal(2, Directory.GetFiles(bankDirectory, "*.xma").Length);
    }

    [Fact]
    public void ExtractEncoded_InvalidPairPreservesExistingOutput()
    {
        using var temp = new TempDirectory();
        var bankDirectory = Path.Combine(temp.Path, "bank");
        Directory.CreateDirectory(bankDirectory);
        var existingPath = Path.Combine(bankDirectory, "0000_Anl_MBF_PitBull.xma");
        var sentinel = "keep me"u8.ToArray();
        File.WriteAllBytes(existingPath, sentinel);
        var pair = BuildPair();
        var invalid = new byte[pair.Index.Length + 1];
        pair.Index.CopyTo(invalid, 0);

        var result = ThawXmaBank.ExtractEncoded(pair.Wad, invalid, "bank", temp.Path);

        Assert.True(result.Skipped);
        Assert.Equal(sentinel, File.ReadAllBytes(existingPath));
        Assert.Single(Directory.GetFiles(bankDirectory));
    }

    [Fact]
    public void ConvertToWav_StagesEveryTargetAndValidatesAudioShape()
    {
        using var temp = new TempDirectory();
        var pair = BuildPair();
        var calls = 0;

        var result = ThawXmaBank.ConvertToWav(
            pair.Wad,
            pair.Index,
            "bank",
            temp.Path,
            Transcode);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(2, result.SamplesWritten);
        Assert.Equal(2, calls);
        var outputs = Directory.GetFiles(Path.Combine(temp.Path, "bank"), "*.wav");
        Assert.Equal(2, outputs.Length);
        Assert.All(outputs, path => Assert.True(RiffWaveReader.TryRead(File.ReadAllBytes(path), out _)));
        Assert.DoesNotContain(Directory.GetFiles(Path.Combine(temp.Path, "bank")),
            static path => Path.GetFileName(path).StartsWith('.'));
        return;

        bool Transcode(string inputPath, string outputPath, out string error)
        {
            calls++;
            var input = File.ReadAllBytes(inputPath);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(input, 0, 4));
            Assert.StartsWith(".", Path.GetFileName(outputPath));
            WavWriter.WritePcm16(outputPath, 22_050, 1, [0, 1, -1, 2]);
            error = "";
            return true;
        }
    }

    [Fact]
    public void ConvertToWav_DecoderFailurePreservesExistingTargetAndCleansStage()
    {
        using var temp = new TempDirectory();
        var bankDirectory = Path.Combine(temp.Path, "bank");
        Directory.CreateDirectory(bankDirectory);
        var existingPath = Path.Combine(bankDirectory, "0000_Anl_MBF_PitBull.wav");
        var sentinel = "existing wave"u8.ToArray();
        File.WriteAllBytes(existingPath, sentinel);
        var pair = BuildPair();

        var result = ThawXmaBank.ConvertToWav(
            pair.Wad,
            pair.Index,
            "bank",
            temp.Path,
            Fail);

        Assert.False(result.Success);
        Assert.False(result.Skipped);
        Assert.Equal("synthetic decoder failure", result.ErrorMessage);
        Assert.Equal(sentinel, File.ReadAllBytes(existingPath));
        Assert.Single(Directory.GetFiles(bankDirectory));
        return;

        static bool Fail(string inputPath, string outputPath, out string error)
        {
            File.WriteAllBytes(outputPath, "partial"u8.ToArray());
            error = "synthetic decoder failure";
            return false;
        }
    }

    [CorpusFact]
    public void Probe_BothCorpusPairs_ConsumesAll3703StreamsExactly()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var expected = new[]
        {
            (Name: "music_xma.wad", Count: 111, IndexSize: 2_224, DataSize: 398_540_800L),
            (Name: "xma.wad", Count: 3_592, IndexSize: 71_844, DataSize: 118_450_176L)
        };
        var banks = new List<ThawXmaBankInfo>();
        foreach (var item in expected)
        {
            var path = paths.FindSampleFile(ThawXenBuild, item.Name);
            Assert.NotNull(path);
            var bank = ThawXmaBank.Probe(path);
            Assert.NotNull(bank);
            Assert.Equal(item.Count, bank.Samples.Count);
            Assert.Equal(item.IndexSize, bank.IndexSize);
            Assert.Equal(item.DataSize, bank.DataSize);
            Assert.Equal(item.DataSize, new FileInfo(path).Length);
            banks.Add(bank);
        }

        var samples = banks.SelectMany(static bank => bank.Samples).ToArray();
        Assert.Equal(3_703, samples.Length);
        Assert.Equal(516_990_976, banks.Sum(static bank => bank.DataSize));
        Assert.Equal(2_425, samples.Count(static sample => sample.HasResolvedName));
        Assert.Equal(1_278, samples.Count(static sample => !sample.HasResolvedName));
        Assert.Equal(3_592, samples.Count(static sample => sample.SampleRate == 22_050));
        Assert.Equal(111, samples.Count(static sample => sample.SampleRate == 48_000));
        Assert.Equal(3_592, samples.Count(static sample => sample.Channels == 1));
        Assert.Equal(111, samples.Count(static sample => sample.Channels == 2));
        Assert.Equal(3_609, samples.Count(static sample => sample.Flags == 0));
        Assert.Equal(64, samples.Count(static sample => sample.Flags == 0x0100));
        Assert.Equal(30, samples.Count(static sample => sample.Flags == 0x0200));
        Assert.All(samples.Where(static sample => !sample.HasResolvedName), sample =>
            Assert.Equal($"0x{sample.NameHash:X8}", sample.Name));
    }

    [CorpusFact]
    public void ConvertSingleToWav_RealCorpusStream_DecodesThroughFfmpeg()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        Assert.SkipWhen(SfdConverter.FindFfmpeg() == null, "ffmpeg not available");
        var path = paths.FindSampleFile(ThawXenBuild, "xma.wad");
        Assert.NotNull(path);
        var bank = ThawXmaBank.Probe(path);
        Assert.NotNull(bank);
        var sample = bank.Samples.MinBy(static item => item.CompressedSize);
        Assert.NotNull(sample);
        using var temp = new TempDirectory();

        var output = ThawXmaBank.ConvertSingleToWav(path, sample.Index, temp.Path);

        Assert.NotNull(output);
        Assert.True(RiffWaveReader.TryRead(File.ReadAllBytes(output), out var wave));
        Assert.Equal(sample.SampleRate, wave.SampleRate);
        Assert.Equal(sample.Channels, wave.Channels);
        Assert.True(wave.DataLength > 0);
    }

    private static TestPair BuildPair()
    {
        Assert.Equal(KnownName, QbKeyLookup.TryResolve(KnownHash));
        var unknownHash = FindUnknownHash();
        var wad = new byte[4096];
        WritePacket(wad.AsSpan(0, 2048), 0x11);
        WritePacket(wad.AsSpan(2048, 2048), 0x22);

        var index = new byte[44];
        BinaryPrimitives.WriteUInt32BigEndian(index, 2);
        WriteEntry(index.AsSpan(4, 20), KnownHash, 2048);
        WriteEntry(index.AsSpan(24, 20), unknownHash, 0);
        return new TestPair(wad, index, unknownHash);
    }

    private static void WriteEntry(Span<byte> entry, uint nameHash, uint offset)
    {
        BinaryPrimitives.WriteUInt32BigEndian(entry, nameHash);
        BinaryPrimitives.WriteUInt32BigEndian(entry[4..], offset);
        BinaryPrimitives.WriteUInt32BigEndian(entry[8..], 2048);
        BinaryPrimitives.WriteUInt32BigEndian(entry[12..], 22_050);
        BinaryPrimitives.WriteUInt16BigEndian(entry[16..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(entry[18..], 1);
    }

    private static void WritePacket(Span<byte> packet, byte payloadByte)
    {
        packet.Clear();
        packet[0] = 0x08;
        packet[4] = payloadByte;
    }

    private static uint FindUnknownHash()
    {
        for (var hash = uint.MaxValue; hash > KnownHash; hash--)
        {
            if (QbKeyLookup.TryResolve(hash) == null)
                return hash;
        }

        throw new InvalidOperationException("Could not find an unused test QBKey");
    }

    private sealed record TestPair(byte[] Wad, byte[] Index, uint UnknownHash);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"nmt-thaw-xma-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
