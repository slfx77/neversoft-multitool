using System.Buffers.Binary;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.Nds;

namespace NeversoftMultitool.Tests.Core.Formats.Nds;

/// <summary>
///     Pins the two Nintendo Nitro wave formats the DS carts use: <c>SWAV</c>
///     effects inside the GOB container and <c>STRM</c> music inside the cart's
///     <c>SDAT</c> sound archive.
///
///     The corpus checks lean on an identity the format itself asserts — a STRM's
///     block table has to reproduce its declared sample count, and a decode has to
///     produce exactly that many frames — so a mis-read field cannot pass.
/// </summary>
public sealed class NdsAudioTests(TestPaths paths)
{
    private const string Sk8landBuild = "Tony Hawk's American Sk8land (2005-11-15, DS - Final)";
    private const string Sk8landRom = "Tony Hawk's American Sk8land (USA).nds";
    private const string SdatPath = "vvobj/generated/sound/sound_stream.sdat";

    [Fact]
    public void NitroAdpcm_UsesSeparateTruncatingDivisions()
    {
        // GBATEK divides the step by 8, 4, 2 and 1 separately, each truncating.
        // Nibble 7 (bits 1|2|4) with step 7: 7/8 + 7/4 + 7/2 + 7 = 0+1+3+7 = 11,
        // whereas the common "((n&7)*2+1)*step/8" one-liner gives 15*7/8 = 13.
        // The block header seeds predictor 0, index 0 (step 7).
        var block = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x07 };
        var samples = new short[2];
        Assert.Equal(2, NitroAdpcm.Decode(block, samples));
        Assert.Equal(11, samples[0]);
        Assert.NotEqual(13, samples[0]);
    }

    [Fact]
    public void NitroAdpcm_SaturatesAtPlusOrMinus0X7Fff()
    {
        // Seed the predictor near the ceiling with the largest step, then push up.
        var block = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(block, 0x7000u | (88u << 16));
        block[4] = 0x77; // two large positive nibbles
        block[5] = 0x77;
        var samples = new short[4];
        NitroAdpcm.Decode(block, samples);
        Assert.All(samples, s => Assert.InRange(s, (short)-0x7FFF, (short)0x7FFF));
        Assert.Equal(0x7FFF, samples[^1]);
    }

    [Fact]
    public void NitroAdpcm_BlockOfNBytesYieldsNMinusFourTimesTwoSamples()
    {
        Assert.Equal(1016, NitroAdpcm.SampleCount(512));
        Assert.Equal(0, NitroAdpcm.SampleCount(4));
        Assert.Equal(0, NitroAdpcm.SampleCount(0));
    }

    [Fact]
    public void SwavFile_RejectsAPayloadTheLoopWordsDoNotDescribe()
    {
        var swav = BuildSwav(payloadBytes: 32);
        // loopLength is in 32-bit words; claim one word too many.
        BinaryPrimitives.WriteUInt32LittleEndian(swav.AsSpan(16 + 8 + 8), 9);
        var ex = Assert.Throws<InvalidDataException>(() => SwavFile.Parse(swav));
        Assert.Contains("payload bytes", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SwavFile_ParsesAndDecodesASyntheticWave()
    {
        var swav = SwavFile.Parse(BuildSwav(payloadBytes: 32));
        Assert.Equal(NitroWaveType.Adpcm, swav.WaveType);
        Assert.Equal(22050, swav.SampleRate);
        Assert.Equal(32, swav.Payload.Length);
        Assert.Equal(NitroAdpcm.SampleCount(32), swav.Decode().Length);
    }

    [CorpusFact]
    public void RealCart_DecodesEverySwavInTheGobContainer()
    {
        var romPath = paths.FindSampleFile(Sk8landBuild, Sk8landRom);
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        using var gob = cart!.TryOpenNested(cart.FindByPath("vvobj/generated/gob/main.gob")!);
        Assert.NotNull(gob);

        var waves = 0;
        var samples = 0L;
        foreach (var entry in gob!.Entries)
        {
            if (!entry.Name.EndsWith(".swav", StringComparison.Ordinal))
                continue;
            var swav = SwavFile.Parse(gob.ReadEntry(entry));
            // The whole corpus is wave type 2 at a sane rate.
            Assert.Equal(NitroWaveType.Adpcm, swav.WaveType);
            Assert.InRange(swav.SampleRate, 8000, 48000);
            var decoded = swav.Decode();
            Assert.NotEmpty(decoded);
            waves++;
            samples += decoded.Length;
        }

        Assert.Equal(335, waves);
        Assert.Equal(8_447_968, samples);
    }

    [CorpusFact]
    public void RealCart_SdatHoldsThirtyNamedStrmTracksThatDecodeToTheirDeclaredLength()
    {
        var romPath = paths.FindSampleFile(Sk8landBuild, Sk8landRom);
        Assert.SkipWhen(romPath == null, "Sk8land ROM sample not available");

        using var cart = ArchiveFileSystem.TryOpen(romPath!);
        var sdatEntry = cart!.FindByPath(SdatPath);
        Assert.NotNull(sdatEntry);

        var sdat = cart.ReadEntry(sdatEntry!);
        Assert.True(SdatArchive.IsSdat(sdat));

        var members = SdatArchive.BuildFileList(sdat);
        Assert.Equal(30, members.Count);
        Assert.All(members, m => Assert.Equal("strm", m.Directory));
        Assert.All(members, m => Assert.EndsWith(".strm", m.Name, StringComparison.Ordinal));
        // Names come from the archive's own SYMB block, not from ordinals.
        Assert.Contains(members, m => m.Name == "STRM_CALIFORNIA.strm");
        Assert.Contains(members, m => m.Name == "STRM_DRUMS_OF_FIRE.strm");

        var totalSamples = 0L;
        foreach (var member in members)
        {
            var strm = StrmFile.Parse(sdat.AsSpan((int)member.Offset, (int)member.Size).ToArray());
            Assert.Equal(NitroWaveType.Adpcm, strm.WaveType);
            Assert.Equal(1, strm.Channels);
            // Parse already enforces the block-table identity; decoding has to
            // produce exactly the sample count the header declares.
            Assert.Equal(strm.SampleCount * strm.Channels, strm.Decode().Length);
            totalSamples += strm.SampleCount;
        }

        Assert.Equal(79_347_626, totalSamples);
    }

    /// <summary>Minimal SWAV: 16-byte file header + DATA block + ADPCM payload.</summary>
    private static byte[] BuildSwav(int payloadBytes)
    {
        var dataBlock = 8 + 12 + payloadBytes;
        var file = new byte[16 + dataBlock];
        "SWAV"u8.CopyTo(file);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(4), 0xFEFF);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(6), 0x0100);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(12), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(14), 1);

        "DATA"u8.CopyTo(file.AsSpan(16));
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(20), (uint)dataBlock);
        file[24] = (byte)NitroWaveType.Adpcm;
        file[25] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(26), 22050);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(28), 760);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(30), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(32), (uint)(payloadBytes / 4));
        return file;
    }
}
