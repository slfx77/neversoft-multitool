using System.Buffers.Binary;
using System.Security.Cryptography;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64AdpcmDecoderTests(TestPaths paths)
{
    private const string Thps2Build = "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)";
    private const string Thps2Rom = "Tony Hawk's Pro Skater 2 (USA).z64";
    private const string Thps3Build = "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)";
    private const string Thps3Rom = "Tony Hawk's Pro Skater 3 (USA).z64";

    [Fact]
    public void Decode_OracleVectors_PinNibbleOrderHistoryRecurrenceAndSaturation()
    {
        var book = CreateOracleBook();
        Assert.Equal(
            "E0F2F34BD03D16B2B2544A42506158F013680C133ADE5A42293D23EEFABA1F3A",
            HashBookBigEndian(book));

        AssertVector(
            book,
            "00 78 F1 2E 3D 4C 5B 6A 90",
            [7, -8, -1, 1, 2, -2, 3, -3, 4, -4, 5, -5, 6, -6, -7, 0],
            "E03B112304C5A964F7D50F041A8008F703CA9F8D3F7CB47D933927C3399875A6");
        AssertVector(
            book,
            "00 00 00 00 00 00 00 00 F1 01 00 00 00 00 00 00 00 00",
            [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 1,
                -1, -2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
            "0ABC4F63E269ECD87BB74567F79AEAC8420973E1A320EAB76D209A591F48CC5C");
        AssertVector(
            book,
            "02 00 00 00 23 1F 00 00 00",
            [0, 0, 0, 0, 0, 0, 2, 5, 6, 2, -1, -1, 0, 0, 0, 0],
            "361BA20F63802CDF1B8BA7B533AC48377343BFAD6AA67F9B5CABFEB574A4231B");
        AssertVector(
            book,
            "C3 00 00 00 78 00 00 00 00",
            [0, 0, 0, 0, 0, 0, 28672, -32768, 32767, -32768, 14, -14, 0, 0, 0, 0],
            "6AD4B4EF8C0B6D017980C4A957F3BB8A3123F8C145C3236ED58F06C8BB3A06EE");
    }

    [Fact]
    public void Decode_OracleWrapVectors_PinAbi1Signed32Accumulator()
    {
        var coefficients = new short[64];
        Array.Fill(coefficients, short.MaxValue, 8, 8);
        var book = new N64SoundToolsAdpcmBook(2, 4, coefficients);

        AssertVector(
            book,
            "C0 77 77 77 77 77 77 77 77",
            [28672, 32767, 32767, -32768, -32768, 32767, 32767, -32768,
                -32768, -32768, 32767, 32767, -32768, -32768, 32767, 32767],
            "7744F3C2A1E4228779FEA1F98B82708A75D4CCFFA03B0DB1552433B0CE8D9F50");
        AssertVector(
            book,
            "C0 88 88 88 88 88 88 88 88",
            [-32768, -32768, 32767, 32767, -32704, -32768, 32767, 32767,
                32767, -32768, -32768, 32767, 32767, -32720, -32768, 32767],
            "618E3F922283AC3DDA6C37BC38856628A89870E69497165D2ED5E97126B056B9");
    }

    [Fact]
    public void Decode_EmptyPayloadWithValidBook_ReturnsEmpty()
    {
        Assert.Empty(N64AdpcmDecoder.Decode([], CreateOracleBook()));
    }

    [Fact]
    public void Decode_MalformedLengthBookAndHeaders_FailClosed()
    {
        var validBook = CreateOracleBook();
        Assert.Throws<ArgumentNullException>(() => N64AdpcmDecoder.Decode([], null!));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode(new byte[8], validBook));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode([], validBook with { Order = 1 }));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode([], validBook with { PredictorCount = 0 }));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode([], validBook with { PredictorCount = 17 }));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode([], validBook with { Coefficients = null! }));
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode([], validBook with { Coefficients = new short[63] }));

        foreach (var scale in new byte[] { 13, 14, 15 })
        {
            var frame = new byte[9];
            frame[0] = (byte)(scale << 4);
            Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode(frame, validBook));
        }

        var badPredictor = new byte[9];
        badPredictor[0] = 4;
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode(badPredictor, validBook));

        var lateBadHeader = new byte[18];
        lateBadHeader[9] = 0xD0;
        Assert.Throws<InvalidDataException>(() => N64AdpcmDecoder.Decode(lateBadHeader, validBook));
    }

    [CorpusFact]
    public void Decode_Thps3Wave221_MatchesAbi1RuntimeGoldenAndClampContext()
    {
        var fixture = LoadWaveFixture(Thps3Build, Thps3Rom, 221);
        Assert.Equal(0x9230, fixture.Wave.DescriptorOffset);
        Assert.Equal(0x5F4A70u, fixture.Wave.WaveBase);
        Assert.Equal(0xB4u, fixture.Wave.WaveLength);
        Assert.Equal(
            "58FCC2F45B909DC728EE036F9DC865A4C75A6D38AE5316BD0C15FEF3D66C94AD",
            HashBytes(fixture.Encoded));
        Assert.Equal(
            "7867B11239A9D3CC26388E489521B7D02B1696D8D07DBCFC8415938EA3CB3149",
            HashBookBigEndian(fixture.Wave.Book));

        var pcm = N64AdpcmDecoder.Decode(fixture.Encoded, fixture.Wave.Book);

        Assert.Equal(320, pcm.Length);
        Assert.Equal(
            "FB5D1A6718250BF978FC3C63B354199EE065437D8A46B0EB22C6D4F031D23E16",
            HashPcmLittleEndian(pcm));
        Assert.Equal(
            new short[] { 5220, 7734, -14492, 26483, -29921, 30218, -32768,
                25140, -18519, 17630, -16573, 13584, -8444 },
            pcm[35..48]);
    }

    [CorpusFact]
    public void Decode_Thps3Wave129_MatchesAbi1RuntimeGoldenAcrossClippedHistory()
    {
        var fixture = LoadWaveFixture(Thps3Build, Thps3Rom, 129);
        Assert.Equal(0x5400, fixture.Wave.DescriptorOffset);
        Assert.Equal(0x52D900u, fixture.Wave.WaveBase);
        Assert.Equal(0x948u, fixture.Wave.WaveLength);
        Assert.Equal(
            "F0200DCB465930B86FD80A3DFB0EEC95C6BE981305131B866C5053426FA1C962",
            HashBytes(fixture.Encoded));
        Assert.Equal(
            "ABE95235791D2884429CC177BD889BBDF280A5B11AB9117DF1532D977D3F1634",
            HashBookBigEndian(fixture.Wave.Book));

        var pcm = N64AdpcmDecoder.Decode(fixture.Encoded, fixture.Wave.Book);

        Assert.Equal(4_224, pcm.Length);
        Assert.Equal(
            "D5EE6F35D5DAC8BF04420DD40ADAF022CEECE1E1B51C56BC65C36219C212F90A",
            HashPcmLittleEndian(pcm));
    }

    [CorpusFact]
    public void Decode_Thps2Wave258_EmitsStoredFramesOnceAndIgnoresLoopState()
    {
        var fixture = LoadWaveFixture(Thps2Build, Thps2Rom, 258);
        Assert.Equal(0xAAA0, fixture.Wave.DescriptorOffset);
        Assert.Equal(0x241C60u, fixture.Wave.WaveBase);
        Assert.Equal(0x2CEEu, fixture.Wave.WaveLength);
        Assert.Equal(
            "B3D2BA2AB599CD8DC8A3D9632B4581F1E20D79626178763E43BF6B93641E5151",
            HashBytes(fixture.Encoded));
        Assert.Equal(
            "593FC1387353827E209B9CE86520C3EF401F25279EE956EFF1C37DFF999A3C34",
            HashBookBigEndian(fixture.Wave.Book));
        Assert.NotNull(fixture.Wave.Loop);
        Assert.Equal(105u, fixture.Wave.Loop!.Start);
        Assert.Equal(20_265u, fixture.Wave.Loop.End);

        var pcm = N64AdpcmDecoder.Decode(fixture.Encoded, fixture.Wave.Book);

        Assert.Equal(20_448, pcm.Length);
        Assert.Equal(
            "C1EE48395815DD022416A3DD739F882DE6ED377FC36B6FBE6CC75E44A6E80755",
            HashPcmLittleEndian(pcm));
        Assert.Equal(fixture.Wave.Loop.State, pcm[96..112]);
    }

    [CorpusFact]
    public void FrameHeaders_FourRomCorpus_PinsStrictPredictorAndScaleDomain()
    {
        (string Build, string Rom, long Frames)[] corpus =
        [
            ("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
                "Tony Hawk's Pro Skater (USA).z64", 293_241),
            (Thps2Build, Thps2Rom, 801_364),
            (Thps3Build, Thps3Rom, 737_934),
            ("Spider-Man (2000-11-21, N64 - Final)",
                "Spider-Man (USA).z64", 1_558_368)
        ];
        long[] predictorCounts = new long[16];
        long[] scaleCounts = new long[16];
        long totalFrames = 0;

        foreach (var expected in corpus)
        {
            var romPath = paths.FindSampleFile(expected.Build, expected.Rom);
            Assert.SkipWhen(romPath == null, $"{expected.Build} ROM sample not available");
            var sources = N64AudioInspectCommand.ResolveSources(romPath!, wavePath: null);
            var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
            long buildFrames = 0;
            foreach (var wave in bank.PointerBank.Waves)
            {
                var encoded = sources.WaveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength);
                for (var offset = 0; offset < encoded.Length; offset += N64AdpcmDecoder.FrameSize)
                {
                    predictorCounts[encoded[offset] & 0x0F]++;
                    scaleCounts[encoded[offset] >> 4]++;
                    buildFrames++;
                }
            }

            Assert.Equal(expected.Frames, buildFrames);
            totalFrames += buildFrames;
        }

        Assert.Equal(3_390_907, totalFrames);
        Assert.Equal(new long[] { 377_659, 833_492, 711_085, 1_468_671 }, predictorCounts[..4]);
        Assert.All(predictorCounts[4..], static value => Assert.Equal(0, value));
        Assert.Equal(
            new long[] { 10_843, 12_034, 27_747, 44_321, 62_909, 96_071, 151_361,
                242_703, 385_434, 643_774, 955_497, 638_914, 119_299 },
            scaleCounts[..13]);
        Assert.All(scaleCounts[13..], static value => Assert.Equal(0, value));
    }

    private WaveFixture LoadWaveFixture(string buildName, string romName, int waveIndex)
    {
        var romPath = paths.FindSampleFile(buildName, romName);
        Assert.SkipWhen(romPath == null, $"{buildName} ROM sample not available");
        var sources = N64AudioInspectCommand.ResolveSources(romPath!, wavePath: null);
        var bank = N64SoundToolsBank.Parse(sources.PointerData, sources.WaveData);
        var wave = bank.PointerBank.Waves[waveIndex];
        var encoded = sources.WaveData.AsSpan((int)wave.WaveBase, (int)wave.WaveLength).ToArray();
        return new WaveFixture(wave, encoded);
    }

    private static N64SoundToolsAdpcmBook CreateOracleBook()
    {
        var coefficients = new short[64];
        coefficients[16] = 1;
        coefficients[17] = 2048;
        coefficients[25] = -2048;
        coefficients[40] = 2048;
        coefficients[41] = 1024;
        coefficients[48] = 32767;
        coefficients[49] = -32768;
        coefficients[50] = 1;
        coefficients[51] = -1;
        return new N64SoundToolsAdpcmBook(2, 4, coefficients);
    }

    private static void AssertVector(
        N64SoundToolsAdpcmBook book,
        string encodedHex,
        short[] expected,
        string expectedHash)
    {
        var actual = N64AdpcmDecoder.Decode(Convert.FromHexString(encodedHex.Replace(" ", "")), book);
        Assert.Equal(expected, actual);
        Assert.Equal(expectedHash, HashPcmLittleEndian(actual));
    }

    private static string HashBookBigEndian(N64SoundToolsAdpcmBook book)
    {
        var bytes = new byte[book.Coefficients.Count * 2];
        for (var i = 0; i < book.Coefficients.Count; i++)
            BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(i * 2), book.Coefficients[i]);
        return HashBytes(bytes);
    }

    internal static string HashPcmLittleEndian(IReadOnlyList<short> samples)
    {
        var bytes = new byte[samples.Count * 2];
        for (var i = 0; i < samples.Count; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return HashBytes(bytes);
    }

    private static string HashBytes(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes));

    private sealed record WaveFixture(N64SoundToolsWaveDescriptor Wave, byte[] Encoded);
}
