using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Nds;

/// <summary>
///     Pins the studio's own DS streaming format — the Downhill Jam and Proving
///     Ground soundtracks, which are the only music in those carts and were
///     unreachable before.
///
///     The corpus checks lean on things the audio itself asserts rather than on
///     golden bytes: the header's size identities are exact, and the per-block codec
///     reset shows up as block boundaries being SMOOTHER than ordinary audio. Decode
///     it as one continuous stream instead and the predictor drifts, the boundaries
///     jump and the signal saturates — so these assertions fail loudly for the one
///     mistake this format actually invites.
/// </summary>
public sealed class HwasStreamTests(TestPaths paths)
{
    private const string DhjBuild = "Tony Hawk's Downhill Jam (2006-10-24, DS - Final)";
    private const string DhjRom = "Tony Hawk's Downhill Jam (USA).nds";
    private const string PgBuild = "Tony Hawk's Proving Ground (2007-10-15, DS - Final)";
    private const string PgRom = "Tony Hawk's Proving Ground (USA).nds";

    [Fact]
    public void Decode_AccumulatesFourSeparatelyTruncatingShifts()
    {
        // Code 7 (bits 1|2|4) at step 7: 7>>3 + 7 + 7>>1 + 7>>2 = 0 + 7 + 3 + 1 = 11.
        // The widely copied one-liner ((code & 7) * 2 + 1) * step / 8 gives 13.
        var stream = HwasStream.Parse(BuildStream([0x07]));
        Assert.Equal(11, stream.Decode()[0]);
    }

    [Fact]
    public void Decode_ReadsTheLowNibbleFirst()
    {
        // 0x87 packs code 7 in the low nibble and code 8 in the high. From silence,
        // code 7 moves the predictor +11 while code 8 — negative, magnitude
        // step >> 3 — moves it nowhere at all. So the first sample is 11 read
        // low-nibble-first and 0 read high-first; the two orders cannot both be
        // right. (The second sample is 9, not -11: code 7 also advanced the step
        // index to 8, so code 8's magnitude is 16 >> 3 rather than 7 >> 3.)
        var samples = HwasStream.Parse(BuildStream([0x87])).Decode();
        Assert.Equal(11, samples[0]);
        Assert.Equal(9, samples[1]);
    }

    [Fact]
    public void Decode_RestartsThePredictorAtEveryBlockBoundary()
    {
        // Two blocks of the largest positive code: without the reset the second
        // block continues climbing from where the first ended.
        var payload = new byte[128];
        Array.Fill(payload, (byte)0x77);
        var stream = HwasStream.Parse(BuildStream(payload, blockSize: 64));
        var samples = stream.Decode();
        Assert.Equal(samples[0], samples[64 * 2]);
        Assert.True(samples[64 * 2 - 1] > samples[64 * 2]);
    }

    [Fact]
    public void Parse_RejectsAStoredSizeThatIsNotTheFileLength()
    {
        var file = BuildStream([0x00]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), 0x1000);
        Assert.Throws<InvalidDataException>(() => HwasStream.Parse(file));
    }

    [Fact]
    public void Parse_RejectsAPayloadLongerThanWhatIsStored()
    {
        var file = BuildStream([0x00]);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(24), 0x1000);
        Assert.Throws<InvalidDataException>(() => HwasStream.Parse(file));
    }

    [CorpusTheory]
    [InlineData(DhjBuild, DhjRom, "vvobj/generated/gob/main.gob", 22, 31_076_086)]
    [InlineData(PgBuild, PgRom, "gob/mainUS.gob", 13, 26_015_965)]
    public void RealCart_EveryStreamsHeaderIdentitiesHold(
        string build, string rom, string gobPath, int expectedFiles, int expectedBytes)
    {
        var romPath = paths.FindSampleFile(build, rom);
        Assert.SkipWhen(romPath == null, $"{build} ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath(gobPath)!);
        Assert.NotNull(gob);

        var files = 0;
        var payload = 0L;
        foreach (var entry in gob!.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!HwasStream.IsHwas(data))
                continue;

            var stream = HwasStream.Parse(data);
            files++;
            payload += stream.DataBytes;

            Assert.Equal(16384, stream.BlockSize);
            Assert.Equal(22019, stream.SampleRate);
            Assert.Equal(1, stream.Channels);
            // The stored region is the payload rounded up to a 512-byte boundary, and
            // the header is exactly one such boundary. Both hold for every file.
            var stored = (stream.DataBytes + 511) / 512 * 512;
            Assert.Equal(512 + stored, data.Length);
            // Padding always exists: no file's payload fills its last block.
            Assert.NotEqual(0, stream.DataBytes % stream.BlockSize);
            Assert.All(data.AsSpan(28, 512 - 28).ToArray(), b => Assert.Equal(0, b));
        }

        Assert.Equal(expectedFiles, files);
        Assert.Equal(expectedBytes, payload);
    }

    /// <summary>
    ///     The audio itself confirms the per-block reset. Sampling the step across
    ///     every block boundary against a step from the middle of each block, the
    ///     boundaries come out FAR smoother — the codec restarting from silence is
    ///     what makes them so. A continuous decode inverts this: the predictor carries
    ///     a running offset into each new block and the boundary is where it jumps.
    /// </summary>
    [CorpusFact]
    public void RealCart_BlockBoundariesAreSmootherThanOrdinaryAudio()
    {
        var romPath = paths.FindSampleFile(DhjBuild, DhjRom);
        Assert.SkipWhen(romPath == null, "Downhill Jam ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath("vvobj/generated/gob/main.gob")!);

        var boundaries = 0L;
        var middles = 0L;
        var boundaryCount = 0;
        var middleCount = 0;
        var saturated = 0L;
        var total = 0L;
        foreach (var entry in gob!.Entries)
        {
            byte[] data;
            try
            {
                data = gob.ReadEntry(entry);
            }
            catch (InvalidDataException)
            {
                continue;
            }

            if (!HwasStream.IsHwas(data))
                continue;

            var stream = HwasStream.Parse(data);
            var samples = stream.Decode();
            var span = stream.BlockSize * 2;
            total += samples.Length;
            foreach (var sample in samples)
            {
                if (sample is short.MinValue or short.MaxValue)
                    saturated++;
            }

            for (var i = span; i < samples.Length; i += span)
            {
                boundaries += Math.Abs(samples[i] - samples[i - 1]);
                boundaryCount++;
            }

            for (var i = span / 2; i < samples.Length; i += span)
            {
                middles += Math.Abs(samples[i] - samples[i - 1]);
                middleCount++;
            }
        }

        Assert.True(boundaryCount > 100);
        var boundaryMean = (double)boundaries / boundaryCount;
        var middleMean = (double)middles / middleCount;
        // Measured 139 against 3,101 — a factor of 22. The assertion is deliberately
        // far looser than the measurement.
        Assert.True(boundaryMean * 4 < middleMean,
            $"block boundaries mean |delta| {boundaryMean:F1} vs mid-block {middleMean:F1}");
        // A continuous decode saturates two to three orders of magnitude more often.
        Assert.True((double)saturated / total < 0.001,
            $"{saturated} of {total} samples saturated");
    }

    /// <summary>A minimal well-formed stream: the 512-byte header plus a payload.</summary>
    private static byte[] BuildStream(byte[] payload, int blockSize = 16384)
    {
        var stored = (payload.Length + 511) / 512 * 512;
        var file = new byte[512 + stored];
        BinaryPrimitives.WriteUInt32LittleEndian(file, 0x68776173);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4), blockSize);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(8), 22019);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(12), 1);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(20), stored);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(24), payload.Length);
        payload.CopyTo(file.AsSpan(512));
        return file;
    }
}
