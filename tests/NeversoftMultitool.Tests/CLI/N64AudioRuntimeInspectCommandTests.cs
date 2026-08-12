using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.CLI;

public sealed class N64AudioRuntimeInspectCommandTests(TestPaths paths)
{
    private const uint Z64Magic = 0x80371240;
    private const int CountryCodeRomOffset = 0x3E;
    private const string SharedRateArgumentHex = "24025622AFA20018";
    private const string SharedWrapperHeadSha256 =
        "52A173F6EFBDFECCD7CE176A7D14EFD7F1721B198F80DF7D29543CF82818C2C1";
    private const string SharedBuilderHeadSha256 =
        "69EF69F9E57EE9CFD48B3A83ECAD6C9735ED17108F8E1BDCC66D995F76ED83AE";
    private const string SharedBuilderStoreSha256 =
        "8D85BA038FB0A33714B01A675670BE385DFB4708F5575A9AE636D0FFA99BEE6D";
    private const string SharedClockSha256 =
        "32DFAC3B68C9D3F2A07544A30DC6FE4FB80F0588E08CADC9C23EB975F0BF3678";

    public static TheoryData<string, int, int> KnownBootEvidence() => new()
    {
        { N64SoundToolsRuntimeProfileResolver.Thps1BootSha256,
            N64SoundToolsRuntimeProfileResolver.Thps1ClockRomOffset,
            N64SoundToolsRuntimeProfileResolver.Thps1AiRoutineRomOffset },
        { N64SoundToolsRuntimeProfileResolver.Thps2BootSha256,
            N64SoundToolsRuntimeProfileResolver.Thps2ClockRomOffset,
            N64SoundToolsRuntimeProfileResolver.Thps2AiRoutineRomOffset },
        { N64SoundToolsRuntimeProfileResolver.Thps3BootSha256,
            N64SoundToolsRuntimeProfileResolver.Thps3ClockRomOffset,
            N64SoundToolsRuntimeProfileResolver.Thps3AiRoutineRomOffset },
        { N64SoundToolsRuntimeProfileResolver.SpiderManBootSha256,
            N64SoundToolsRuntimeProfileResolver.SpiderManClockRomOffset,
            N64SoundToolsRuntimeProfileResolver.SpiderManAiRoutineRomOffset }
    };

    [Fact]
    public void MixerCalculation_UsesRoundedNtscAiDivisorAndIntegerReturn()
    {
        var mixer = N64SoundToolsRuntimeProfileResolver.CalculateMixerProfile(
            N64SoundToolsRuntimeProfileResolver.NtscVideoClockHz,
            N64SoundToolsRuntimeProfileResolver.RequestedMixerRateHz);

        Assert.Equal("romGlobalMixerOutput", mixer.Scope);
        Assert.Equal("NTSC", mixer.VideoStandard);
        Assert.Equal(48_681_812u, mixer.VideoClockHz);
        Assert.Equal(22_050u, mixer.RequestedRateHz);
        Assert.Equal(2_208u, mixer.AiDacRateDivisor);
        Assert.Equal(2_207u, mixer.AiDacRateRegisterValue);
        Assert.Equal(22_047u, mixer.AiFrequencyReturnHz);
        Assert.Equal(2_036u, mixer.VideoClockHz % mixer.AiDacRateDivisor);
    }

    [Theory]
    [MemberData(nameof(KnownBootEvidence))]
    public void Resolver_RequiresExactBootNtscCountryPinnedClockAndAiRoutine(
        string bootSha256,
        int clockRomOffset,
        int aiRoutineRomOffset)
    {
        var rom = BuildSyntheticKnownRom(clockRomOffset, aiRoutineRomOffset);
        var aiRoutineSha256 = Hash(rom.AsSpan(
            aiRoutineRomOffset,
            N64SoundToolsRuntimeProfileResolver.AiRoutineLength));
        var profile = N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
            rom,
            bootSha256,
            clockRomOffset,
            aiRoutineRomOffset,
            aiRoutineSha256);

        Assert.Equal(bootSha256, profile.BootSha256);
        Assert.Equal(aiRoutineSha256, profile.AiRoutineSha256);
        Assert.Equal(clockRomOffset, profile.VideoClockRomOffset);
        Assert.Equal(aiRoutineRomOffset, profile.AiRoutineRomOffset);
        Assert.Equal(0x160, profile.AiRoutineLength);
        Assert.Equal(0x45, profile.RomCountryCodeRaw);
        Assert.Equal(22_047u, profile.MixerProfile.AiFrequencyReturnHz);
        Assert.Equal(0x0000000Fu,
            BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(4, sizeof(uint))));

        var wrongCountry = rom.ToArray();
        wrongCountry[CountryCodeRomOffset] = 0x50;
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
                wrongCountry,
                bootSha256,
                clockRomOffset,
                aiRoutineRomOffset,
                aiRoutineSha256));

        var wrongClock = rom.ToArray();
        wrongClock[clockRomOffset + 3] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
                wrongClock,
                bootSha256,
                clockRomOffset,
                aiRoutineRomOffset,
                aiRoutineSha256));

        var wrongRoutine = rom.ToArray();
        wrongRoutine[aiRoutineRomOffset] ^= 1;
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
                wrongRoutine,
                bootSha256,
                clockRomOffset,
                aiRoutineRomOffset,
                aiRoutineSha256));

        var truncated = rom[..(clockRomOffset + sizeof(uint) - 1)];
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
                truncated,
                bootSha256,
                clockRomOffset,
                aiRoutineRomOffset,
                aiRoutineSha256));

        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForKnownBootSha(
                rom,
                new string('0', 64)));
    }

    [Fact]
    public void Resolver_TruncatedPinnedAiRoutineRejects()
    {
        const int clockRomOffset = 0x40;
        const int aiRoutineRomOffset = 0x80;
        var rom = BuildSyntheticKnownRom(clockRomOffset, aiRoutineRomOffset);
        var aiRoutineSha256 = Hash(rom.AsSpan(
            aiRoutineRomOffset,
            N64SoundToolsRuntimeProfileResolver.AiRoutineLength));
        var truncated = rom[..^1];

        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
                truncated,
                N64SoundToolsRuntimeProfileResolver.Thps1BootSha256,
                clockRomOffset,
                aiRoutineRomOffset,
                aiRoutineSha256));
    }

    [Fact]
    public void Json_V1IsDeterministicAndSeparatesMixerOutputFromWaveCuePlayback()
    {
        const int clockRomOffset = 0x40;
        const int aiRoutineRomOffset = 0x80;
        var rom = BuildSyntheticKnownRom(clockRomOffset, aiRoutineRomOffset);
        var aiRoutineSha256 = Hash(rom.AsSpan(
            aiRoutineRomOffset,
            N64SoundToolsRuntimeProfileResolver.AiRoutineLength));
        var profile = N64SoundToolsRuntimeProfileResolver.ResolveForEvidence(
            rom,
            N64SoundToolsRuntimeProfileResolver.Thps1BootSha256,
            clockRomOffset,
            aiRoutineRomOffset,
            aiRoutineSha256);

        var first = N64SoundToolsRuntimeProfileJsonExporter.Serialize("game.z64", profile);
        var second = N64SoundToolsRuntimeProfileJsonExporter.Serialize("game.z64", profile);
        Assert.Equal(first, second);

        using var json = JsonDocument.Parse(first);
        var root = json.RootElement;
        Assert.Equal("neversoft.n64.soundToolsRuntimeProfile",
            root.GetProperty("schema").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("N64 Sound Tools ROM runtime profile", root.GetProperty("format").GetString());
        Assert.Equal("game.z64", root.GetProperty("inputSource").GetString());
        Assert.Equal("resolved", root.GetProperty("profileStatus").GetString());
        Assert.Equal("exactBootSha256NtscCountryClockAndAiRoutine",
            root.GetProperty("detectionBasis").GetString());
        Assert.Equal("boot.bin", root.GetProperty("bootSource").GetString());
        Assert.Equal(profile.BootSha256, root.GetProperty("bootSha256").GetString());
        Assert.Equal(0x45, root.GetProperty("romCountryCodeRaw").GetInt32());
        Assert.Equal(0x3E, root.GetProperty("romCountryCodeOffset").GetInt32());
        Assert.Equal(clockRomOffset, root.GetProperty("videoClockRomOffset").GetInt32());
        Assert.Equal("rawRom", root.GetProperty("aiRoutineSource").GetString());
        Assert.Equal(aiRoutineRomOffset, root.GetProperty("aiRoutineRomOffset").GetInt32());
        Assert.Equal(0x160, root.GetProperty("aiRoutineLength").GetInt32());
        Assert.Equal(aiRoutineSha256, root.GetProperty("aiRoutineSha256").GetString());
        Assert.Equal("unresolved", root.GetProperty("perWaveRateStatus").GetString());
        Assert.Equal("unresolved", root.GetProperty("cueMappingStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("pitchApplicationStatus").GetString());
        Assert.Equal("notApplied", root.GetProperty("loopSchedulingStatus").GetString());
        Assert.Equal("notExecuted", root.GetProperty("playbackStatus").GetString());
        Assert.False(root.TryGetProperty("sampleRate", out _));

        var mixer = root.GetProperty("mixerProfile");
        Assert.Equal("romGlobalMixerOutput", mixer.GetProperty("scope").GetString());
        Assert.Equal("NTSC", mixer.GetProperty("videoStandard").GetString());
        Assert.Equal(48_681_812, mixer.GetProperty("videoClockHz").GetInt32());
        Assert.Equal(22_050, mixer.GetProperty("requestedRateHz").GetInt32());
        Assert.Equal(2_208, mixer.GetProperty("aiDacRateDivisor").GetInt32());
        Assert.Equal(2_207, mixer.GetProperty("aiDacRateRegisterValue").GetInt32());
        Assert.Equal(22_047, mixer.GetProperty("aiFrequencyReturnHz").GetInt32());
        Assert.False(mixer.TryGetProperty("sampleRate", out _));
    }

    [Fact]
    public void Command_IsRomOnlyAndInvalidInputNeverTouchesDestination()
    {
        using var temp = new TempDirectory();
        var malformed = Path.Combine(temp.Path, "malformed.z64");
        var rom = new byte[0x1000];
        BinaryPrimitives.WriteUInt32BigEndian(rom, Z64Magic);
        File.WriteAllBytes(malformed, rom);

        var absent = Path.Combine(temp.Path, "absent", "runtime.json");
        Assert.Equal(1, N64AudioRuntimeInspectCommand.Execute(malformed, absent));
        Assert.False(Directory.Exists(Path.GetDirectoryName(absent)));

        var existing = Path.Combine(temp.Path, "existing.json");
        const string sentinel = "existing output must survive";
        File.WriteAllText(existing, sentinel);
        Assert.Equal(1, N64AudioRuntimeInspectCommand.Execute(malformed, existing));
        Assert.Equal(sentinel, File.ReadAllText(existing));

        var standalone = Path.Combine(temp.Path, "bank.ptr.n64");
        File.WriteAllBytes(standalone, [1, 2, 3, 4]);
        Assert.Equal(1, N64AudioRuntimeInspectCommand.Execute(
            standalone,
            Path.Combine(temp.Path, "standalone.json")));

        var command = N64AudioRuntimeInspectCommand.Create();
        Assert.Equal("n64-audio-runtime-inspect", command.Name);
        foreach (var option in new[] { "--wave", "--pointer", "--sample-rate", "--target" })
        {
            var forbiddenOutput = Path.Combine(temp.Path, $"forbidden-{option[2..]}.json");
            Assert.NotEqual(0, command.Parse(
                [malformed, option, "value", "-o", forbiddenOutput]).Invoke());
            Assert.False(File.Exists(forbiddenOutput));
        }

        var missing = Path.Combine(temp.Path, "missing.z64");
        var missingOutput = Path.Combine(temp.Path, "missing.json");
        Assert.Equal(1, N64AudioRuntimeInspectCommand.Execute(missing, missingOutput));
        Assert.False(File.Exists(missingOutput));
    }

    [Fact]
    public void ProgramRoute_RegistersCommandHelp()
    {
        Assert.Equal(0, Program.Main(["n64-audio-runtime-inspect", "--help"]));
    }

    [CorpusFact]
    public void Command_FourRomCorpusPinsRuntimeProfileAndCompleteSemanticChain()
    {
        foreach (var expected in CorpusExpectations())
        {
            var romPath = paths.FindSampleFile(expected.Build, expected.RomName);
            Assert.SkipWhen(romPath == null, $"{expected.Build} ROM sample not available");
            var rom = File.ReadAllBytes(romPath!);
            Assert.Equal(expected.RomSha256, Hash(rom));
            Assert.Equal(0x45, rom[CountryCodeRomOffset]);

            Assert.True(N64RomArchive.TryReadMasterDirectory(
                rom,
                out _,
                out _,
                out var bootTable));
            var boot = N64RomArchive.ExtractTable(rom, bootTable);
            Assert.Equal(expected.BootSha256, Hash(boot));

            AssertBootSlice(boot, expected.BootRamBase, expected.RateCall);
            Assert.StartsWith(SharedRateArgumentHex,
                Convert.ToHexString(boot.AsSpan(
                    expected.RateCall.Offset,
                    expected.RateCall.Length)));
            AssertBootSlice(boot, expected.BootRamBase, expected.WrapperHead);
            Assert.Equal(SharedWrapperHeadSha256, expected.WrapperHead.Sha256);
            AssertBootSlice(boot, expected.BootRamBase, expected.WrapperPropagation);
            AssertBootSlice(boot, expected.BootRamBase, expected.BuilderHead);
            Assert.Equal(SharedBuilderHeadSha256, expected.BuilderHead.Sha256);
            AssertBootSlice(boot, expected.BootRamBase, expected.BuilderStore);
            Assert.Equal(SharedBuilderStoreSha256, expected.BuilderStore.Sha256);
            AssertBootSlice(boot, expected.BootRamBase, expected.AudThreadPrefix);

            // libmus.h places syn_output_rate at config+0x2C. These raw words
            // pin arg7 -> nested arg6 -> config+0x2C, then aud_thread's load
            // into a0 and the exact jal to libultra osAiSetFrequency.
            Assert.Equal(0x27BDFFC0u,
                ReadU32(boot, expected.WrapperHead.Offset));
            Assert.Equal(0x8FA20058u,
                ReadU32(boot, expected.WrapperPropagation.Offset));
            Assert.Equal(0xAFA20014u,
                ReadU32(boot, expected.WrapperPropagation.Offset + 16));
            Assert.Equal(0x27BDFFD8u,
                ReadU32(boot, expected.BuilderHead.Offset));
            Assert.Equal(0x00808821u,
                ReadU32(boot, expected.BuilderHead.Offset + 8));
            Assert.Equal(0x8FA3003Cu,
                ReadU32(boot, expected.BuilderStore.Offset));
            Assert.Equal(0xAE23002Cu,
                ReadU32(boot, expected.BuilderStore.Offset + 4));
            Assert.Equal(0x00808021u,
                ReadU32(boot, expected.AudThreadPrefix.Offset + 8));
            Assert.Equal(0x8E04002Cu,
                ReadU32(boot, expected.AudThreadPrefix.Offset + 72));
            Assert.Equal(0xAFA20030u,
                ReadU32(boot, expected.AudThreadPrefix.Offset + 88));
            Assert.Equal(EncodeJal(expected.WrapperHead.RamAddress),
                ReadU32(boot, expected.RateCall.Offset + expected.RateCall.Length - 8));
            Assert.Equal(EncodeJal(expected.BuilderHead.RamAddress),
                ReadU32(boot, expected.WrapperPropagation.Offset + 12));
            Assert.Equal(EncodeJal(expected.OsAiSetFrequency.RamAddress),
                ReadU32(boot, expected.AudThreadPrefix.Offset + 76));

            AssertRomSlice(rom, expected.OsAiSetFrequency);
            AssertRomSlice(rom, expected.NtscClock);
            Assert.Equal(SharedClockSha256, expected.NtscClock.Sha256);
            Assert.Equal(48_681_812u, ReadU32(rom, expected.NtscClock.Offset));
            Assert.Equal(1, CountSequence(rom, Convert.FromHexString("02E6D354")));

            // The exact osAiSetFrequency routine computes round(clock/rate),
            // writes divisor-1 to AI_DACRATE, then returns clock/divisor.
            var osAi = expected.OsAiSetFrequency.Offset;
            var clockRamAddress = expected.NtscClock.RamAddress;
            Assert.Equal(0x3C0E0000u | (clockRamAddress >> 16),
                ReadU32(rom, osAi)); // lui t6, hi(osViClock)
            Assert.Equal(0x8DCE0000u | (clockRamAddress & 0xFFFF),
                ReadU32(rom, osAi + 0x04)); // lw t6, lo(osViClock)(t6)
            Assert.Equal(0x44844000u, ReadU32(rom, osAi + 0x08)); // mtc1 a0, f8
            Assert.Equal(0x460A3483u, ReadU32(rom, osAi + 0x30)); // div.s clock, requested
            Assert.Equal(0x3C013F00u, ReadU32(rom, osAi + 0x34)); // float 0.5
            Assert.Equal(0x46049300u, ReadU32(rom, osAi + 0x40)); // add.s +0.5
            Assert.Equal(0x24B9FFFFu, ReadU32(rom, osAi + 0x100)); // divisor - 1
            Assert.Equal(0x3C08A450u, ReadU32(rom, osAi + 0x104)); // AI registers
            Assert.Equal(0xAD190010u, ReadU32(rom, osAi + 0x108)); // AI_DACRATE
            Assert.Equal(0x3C0D0000u | (clockRamAddress >> 16),
                ReadU32(rom, osAi + 0x124)); // lui t5, hi(osViClock)
            Assert.Equal(0x8DAD0000u | (clockRamAddress & 0xFFFF),
                ReadU32(rom, osAi + 0x128)); // lw t5, lo(osViClock)(t5)
            Assert.Equal(0x01A5001Au, ReadU32(rom, osAi + 0x12C)); // clock / divisor
            Assert.Equal(0x00001012u, ReadU32(rom, osAi + 0x130)); // return quotient

            using var temp = new TempDirectory();
            var output = Path.Combine(temp.Path, "nested", "runtime.json");
            Assert.Equal(0, N64AudioRuntimeInspectCommand.Execute(romPath!, output));
            using var json = JsonDocument.Parse(File.ReadAllText(output));
            var root = json.RootElement;
            Assert.Equal(expected.RomName, root.GetProperty("inputSource").GetString());
            Assert.Equal(expected.BootSha256, root.GetProperty("bootSha256").GetString());
            Assert.Equal(expected.OsAiSetFrequency.Sha256,
                root.GetProperty("aiRoutineSha256").GetString());
            Assert.Equal(expected.NtscClock.Offset,
                root.GetProperty("videoClockRomOffset").GetInt32());
            Assert.Equal(expected.OsAiSetFrequency.Offset,
                root.GetProperty("aiRoutineRomOffset").GetInt32());
            Assert.Equal(expected.OsAiSetFrequency.Length,
                root.GetProperty("aiRoutineLength").GetInt32());
            Assert.Equal("resolved", root.GetProperty("profileStatus").GetString());
            Assert.Equal(22_047,
                root.GetProperty("mixerProfile").GetProperty("aiFrequencyReturnHz").GetInt32());
            Assert.Equal("unresolved", root.GetProperty("perWaveRateStatus").GetString());
            Assert.False(root.TryGetProperty("sampleRate", out _));
        }
    }

    private static byte[] BuildSyntheticKnownRom(
        int clockRomOffset,
        int aiRoutineRomOffset)
    {
        var length = Math.Max(
            clockRomOffset + sizeof(uint),
            aiRoutineRomOffset + N64SoundToolsRuntimeProfileResolver.AiRoutineLength);
        var rom = new byte[length];
        BinaryPrimitives.WriteUInt32BigEndian(rom, Z64Magic);
        // The header word at +0x04 is an unrelated value, not osViClock.
        BinaryPrimitives.WriteUInt32BigEndian(rom.AsSpan(4), 0x0000000F);
        rom[CountryCodeRomOffset] = N64SoundToolsRuntimeProfileResolver.NtscCountryCodeRaw;
        for (var index = 0;
             index < N64SoundToolsRuntimeProfileResolver.AiRoutineLength;
             index++)
        {
            rom[aiRoutineRomOffset + index] = unchecked((byte)(index * 37 + 11));
        }
        BinaryPrimitives.WriteUInt32BigEndian(
            rom.AsSpan(clockRomOffset),
            N64SoundToolsRuntimeProfileResolver.NtscVideoClockHz);
        return rom;
    }

    private static void AssertBootSlice(byte[] boot, uint ramBase, SliceExpected expected)
    {
        Assert.Equal(expected.RamAddress, checked(ramBase + (uint)expected.Offset));
        AssertSlice(boot, expected);
    }

    private static void AssertRomSlice(byte[] rom, SliceExpected expected)
    {
        Assert.Equal(expected.RamAddress,
            checked(0x80000400u + (uint)expected.Offset - 0x1000u));
        AssertSlice(rom, expected);
    }

    private static void AssertSlice(byte[] data, SliceExpected expected)
    {
        Assert.InRange(expected.Offset, 0, data.Length - expected.Length);
        Assert.Equal(expected.Sha256,
            Hash(data.AsSpan(expected.Offset, expected.Length)));
    }

    private static string Hash(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data));

    private static uint ReadU32(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, sizeof(uint)));

    private static uint EncodeJal(uint ramAddress) =>
        0x0C000000u | ((ramAddress >> 2) & 0x03FFFFFFu);

    private static int CountSequence(byte[] data, byte[] sequence)
    {
        var count = 0;
        for (var offset = 0; offset <= data.Length - sequence.Length; offset++)
        {
            if (data.AsSpan(offset, sequence.Length).SequenceEqual(sequence))
                count++;
        }

        return count;
    }

    private static CorpusExpected[] CorpusExpectations() =>
    [
        new(
            "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
            "Tony Hawk's Pro Skater (USA).z64",
            "F96E1688A360214844421A230A782A0C0215DDDFAB81F1BFEDADE0364648EE52",
            N64SoundToolsRuntimeProfileResolver.Thps1BootSha256,
            0x80016990,
            new(0x233E8, 0x80039D78, 28,
                "5C034BDD8B6BB34668672FF3411A38BEBAD452E195B86E1DCD82BFCEA915143C"),
            new(0x9C5C8, 0x800B2F58, 4, SharedWrapperHeadSha256),
            new(0x9C6D0, 0x800B3060, 20,
                "549F1955D0BF709949C1871D8B3837F7F80D0BD24EA3B0767B1FDECE4A8FBF26"),
            new(0x9FCD0, 0x800B6660, 12, SharedBuilderHeadSha256),
            new(0x9FDB8, 0x800B6748, 8, SharedBuilderStoreSha256),
            new(0xA4F20, 0x800BB8B0, 92,
                "1818E261D6119C5E88834EF60F87C2444E8394C4CC67ECCF47472EB849A7D084"),
            new(0x3AB0, 0x80002EB0, 352,
                "3EB88FD7E23CC742ABDF7F354BE4C625B23644209C119411543F9F1D2B3D0727"),
            new(0x12708, 0x80011B08, 4, SharedClockSha256)),
        new(
            "Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
            "Tony Hawk's Pro Skater 2 (USA).z64",
            "6AC38612AAAE84F8BBA22A33A165C17FBA3072B16999EDCC9A86AB726008D726",
            N64SoundToolsRuntimeProfileResolver.Thps2BootSha256,
            0x80016B20,
            new(0x14118, 0x8002AC38, 36,
                "FBBE43E3EB730F5C99A732E654E03CD3306766605A577DEB3F9B3E3879931FA7"),
            new(0xBAFD4, 0x800D1AF4, 4, SharedWrapperHeadSha256),
            new(0xBB0DC, 0x800D1BFC, 20,
                "122B3A6A23A1CA148E406739C41C33817AF461A75D348777C5EA68EAC7EE8A68"),
            new(0xBDDB8, 0x800D48D8, 12, SharedBuilderHeadSha256),
            new(0xBDE9C, 0x800D49BC, 8, SharedBuilderStoreSha256),
            new(0xC3000, 0x800D9B20, 92,
                "E37AAD62C2BDBA1D9619DD06E291905E66387FDBF3DD7B066E4548226E360F00"),
            new(0x3BD0, 0x80002FD0, 352,
                "95FED6D48E762F4347165599D111A1485EBE599F15482A03DDF80FC8361FBF33"),
            new(0x12898, 0x80011C98, 4, SharedClockSha256)),
        new(
            "Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
            "Tony Hawk's Pro Skater 3 (USA).z64",
            "93C8A1BE89390EF27E5F2DA709CA16C9A24936538766C6774640CF534C9D2AEE",
            N64SoundToolsRuntimeProfileResolver.Thps3BootSha256,
            0x80016B20,
            new(0x12BF8, 0x80029718, 36,
                "823117BA7ECB8F15F2356DA796F00F800BD2F3F93B147661E6250505E41FC67A"),
            new(0xBFCE4, 0x800D6804, 4, SharedWrapperHeadSha256),
            new(0xBFDEC, 0x800D690C, 20,
                "A5FD263ABAEDE8854BB7B5CD9C18A65A6B109F6A79896998D145468992341359"),
            new(0xC2AC8, 0x800D95E8, 12, SharedBuilderHeadSha256),
            new(0xC2BAC, 0x800D96CC, 8, SharedBuilderStoreSha256),
            new(0xC7D10, 0x800DE830, 92,
                "5E5244AA5EEB393C878A224731EAFBEAA9A5B5FA48C9D2AB1ECE8638DE4AA55C"),
            new(0x3BD0, 0x80002FD0, 352,
                "95FED6D48E762F4347165599D111A1485EBE599F15482A03DDF80FC8361FBF33"),
            new(0x12898, 0x80011C98, 4, SharedClockSha256)),
        new(
            "Spider-Man (2000-11-21, N64 - Final)",
            "Spider-Man (USA).z64",
            "FEFF90ED1201C91FF167D66958048E61C192C9D6A756DDB98F799017AC9CD25C",
            N64SoundToolsRuntimeProfileResolver.SpiderManBootSha256,
            0x80016AE0,
            new(0x1A72C, 0x8003120C, 28,
                "D4C6CD1D3C35AE43969F69269650E6F8E671605BB39516B6692214240CF9FC8B"),
            new(0xC3738, 0x800DA218, 4, SharedWrapperHeadSha256),
            new(0xC3840, 0x800DA320, 20,
                "75379BAF48051DE0D00CA087EF4A41F7EA9E3BE28D34277207E675C02B083D28"),
            new(0xC6488, 0x800DCF68, 12, SharedBuilderHeadSha256),
            new(0xC6570, 0x800DD050, 8, SharedBuilderStoreSha256),
            new(0xCB6D0, 0x800E21B0, 92,
                "EAF7F5CDA2B7D1058AD58780DB4363DDA33608A031FDB7438D93736E8CF2978B"),
            new(0x3BA0, 0x80002FA0, 352,
                "69CE8CC7382E702C0CA49F917577A5296B877378E409F3BD5786DADC1A4A78FD"),
            new(0x12858, 0x80011C58, 4, SharedClockSha256))
    ];

    private sealed record CorpusExpected(
        string Build,
        string RomName,
        string RomSha256,
        string BootSha256,
        uint BootRamBase,
        SliceExpected RateCall,
        SliceExpected WrapperHead,
        SliceExpected WrapperPropagation,
        SliceExpected BuilderHead,
        SliceExpected BuilderStore,
        SliceExpected AudThreadPrefix,
        SliceExpected OsAiSetFrequency,
        SliceExpected NtscClock);

    private sealed record SliceExpected(
        int Offset,
        uint RamAddress,
        int Length,
        string Sha256);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-runtime-inspect-" + Guid.NewGuid().ToString("N"));
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
