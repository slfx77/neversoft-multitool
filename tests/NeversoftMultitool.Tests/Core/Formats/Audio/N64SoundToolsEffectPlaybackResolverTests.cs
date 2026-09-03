using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Formats.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Audio;

public sealed class N64SoundToolsEffectPlaybackResolverTests(TestPaths paths)
{
    [Fact]
    public void Resolve_SignedPtrDetuneInitialNotePitchAndLoop_AreExact()
    {
        var (pointerData, _) = N64SoundToolsBankTests.BuildPair(pointerTail: 0, waveTail: 0);
        var pointer = N64SoundToolsBank.ParsePointer(pointerData);
        var fx = N64SoundToolsFxBank.Parse(
            N64SoundToolsFxBankTests.BuildFxData(
                [BuildEvent(localWave: 0, note: 0x30), BuildEvent(localWave: 1, note: 0x30)],
                [0, 1, 2]),
            pointer);

        var positiveFine = N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 0, 22_047);
        Assert.Equal(0, positiveFine.LocalWaveIndex);
        Assert.Equal(0, positiveFine.PointerWaveIndex);
        Assert.Equal(0xF4, positiveFine.PointerBaseNoteRaw);
        Assert.Equal(-60, positiveFine.PointerBasePitchOffsetSemitones);
        Assert.Equal(127, positiveFine.PointerFineTuneCents);
        Assert.Equal(unchecked((int)0xC12BAE14),
            BitConverter.SingleToInt32Bits(positiveFine.StaticPitchSemitones));
        Assert.Equal(0x3F09BE4E,
            BitConverter.SingleToInt32Bits(positiveFine.CalculatedPitchRatio));
        Assert.Equal(11_863, positiveFine.NearestWavRateHz);
        Assert.Equal(N64SoundToolsEffectPlaybackResolver.NoStoredLoopMode,
            positiveFine.StoredLoopMode);

        var negativeFine = N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 1, 22_047);
        Assert.Equal(1, negativeFine.LocalWaveIndex);
        Assert.Equal(1, negativeFine.PointerWaveIndex);
        Assert.Equal(0, negativeFine.PointerBaseNoteRaw);
        Assert.Equal(-48, negativeFine.PointerBasePitchOffsetSemitones);
        Assert.Equal(-128, negativeFine.PointerFineTuneCents);
        Assert.Equal(unchecked((int)0xBFA3D700),
            BitConverter.SingleToInt32Bits(negativeFine.StaticPitchSemitones));
        Assert.Equal(0x3F6DC159,
            BitConverter.SingleToInt32Bits(negativeFine.CalculatedPitchRatio));
        Assert.Equal(20_476, negativeFine.NearestWavRateHz);
        Assert.Equal(N64SoundToolsEffectPlaybackResolver.InfiniteStoredLoopMode,
            negativeFine.StoredLoopMode);
        Assert.Equal(0u, negativeFine.PointerWave.Loop!.Start);
        Assert.Equal(16u, negativeFine.PointerWave.Loop.End);
        Assert.Equal(uint.MaxValue, negativeFine.PointerWave.Loop.CountRaw);
        Assert.False(negativeFine.VelocitySilencedByPitchLimit);
    }

    [Fact]
    public void Resolve_UnprovenEventsFailClosedAndRuntimePitchLimitIsExplicit()
    {
        var (pointerData, _) = N64SoundToolsBankTests.BuildPair(pointerTail: 0, waveTail: 0);
        var pointer = N64SoundToolsBank.ParsePointer(pointerData);

        var rest = BuildEvent(localWave: 0, note: 0x60);
        var noStop = BuildEvent(localWave: 1, note: 0x30);
        noStop[^1] = 0xE2;
        var tooHigh = BuildEvent(localWave: 0, note: 0x7F);
        var finiteLoop = BuildEvent(localWave: 2, note: 0x2C);
        var fx = N64SoundToolsFxBank.Parse(
            N64SoundToolsFxBankTests.BuildFxData(
                [rest, noStop, tooHigh, finiteLoop], [0, 1, 2]),
            pointer);

        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, -1, 22_047));
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 4, 22_047));
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 0, 22_047));
        Assert.Throws<InvalidDataException>(() =>
            N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 1, 22_047));

        var silenced = N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 2, 22_047);
        Assert.True(silenced.CalculatedPitchRatio > 2.0f);
        Assert.Equal(2.0f, silenced.RuntimePitchRatio);
        Assert.True(silenced.VelocitySilencedByPitchLimit);
        Assert.Equal(44_094, silenced.NearestWavRateHz);

        var finite = N64SoundToolsEffectPlaybackResolver.Resolve(fx, pointer, 3, 22_047);
        Assert.Equal(-15.0f, finite.StaticPitchSemitones);
        Assert.Equal(0x3ED746E0, BitConverter.SingleToInt32Bits(finite.CalculatedPitchRatio));
        Assert.Equal(9_270, finite.NearestWavRateHz);
        Assert.Equal(N64SoundToolsEffectPlaybackResolver.FiniteStoredLoopMode,
            finite.StoredLoopMode);
        Assert.Throws<InvalidDataException>(() => N64AudioDecodeCommand.ResolveWavLoop(finite.PointerWave));
    }

    [CorpusFact]
    public void FinalRomCorpus_AllEffectsResolveAndSelectedLoopWavsCarryExactMetadata()
    {
        CorpusExpected[] expectations =
        [
            new("Tony Hawk's Pro Skater (2000-2-29, N64 - Final)",
                "Tony Hawk's Pro Skater (USA).z64", 2_176, 143, 33, 178,
                76, 33, "-12:178", "11024:178", 0, 11, 16, 21_852, 11_024,
                21_872, "210E2FEDFADC5A7359D74F07896EA5D0F738C9B55CC78214C89BF4D7F3604A54",
                0, 0, 0),
            new("Tony Hawk's Pro Skater 2 (2001-8-21, N64 - Final)",
                "Tony Hawk's Pro Skater 2 (USA).z64", 3_995, 379, 72, 322,
                67, 67, "0:322", "22047:322", 4, 4, 16, 22_676, 22_047,
                22_688, "5535CD1B61A8918D72A0B51CF36C1E86D7812ACC9AAFF660584035FCFD4CEC35",
                622, 34, 15),
            new("Tony Hawk's Pro Skater 3 (2002-8-20, N64 - Final)",
                "Tony Hawk's Pro Skater 3 (USA).z64", 3_346, 257, 50, 186,
                49, 49, "0:186", "22047:186", 2, 73, 16, 23_886, 22_047,
                23_888, "C976338AFDB6BF5FE6726B3185534AB560116347300AD417A12858CF80AE33CA",
                542, 30, 0),
            new("Spider-Man (2000-11-21, N64 - Final)",
                "Spider-Man (USA).z64", 4_347, 996, 165, 994,
                167, 165, "-15:1,-12:510,-11:483", "9270:1,11024:510,11679:483",
                0, 0, 16, 12_390, 11_679,
                12_400, "F554DAA150D323948B982A5B65E779274F75FCF031DFD3FD7CA4B535A646E7C0",
                1_696, 233, 0)
        ];

        var totalAssets = 0;
        var totalWaves = 0;
        var totalStoredLoops = 0;
        var totalEffects = 0;
        var totalLoopedEffects = 0;
        var totalDistinctReferencedLoopWaves = 0;
        var totalCueBanks = 0;
        var totalCues = 0;

        using var temp = new TempDirectory();
        foreach (var expected in expectations)
        {
            var romPath = paths.FindSampleFile(expected.Build, expected.Rom);
            Assert.SkipWhen(romPath == null, $"{expected.Build} ROM sample not available");
            var rom = File.ReadAllBytes(romPath!);
            Assert.True(N64AssetCarver.TryCarve(rom, out var assets));
            Assert.Equal(expected.Assets, assets.Count);

            var boot = Assert.Single(assets, static asset =>
                asset.Path == N64AssetCarver.BootAssetPath).Data;
            var runtime = N64SoundToolsRuntimeProfileResolver.Resolve(rom, boot);
            Assert.Equal(22_050u, runtime.MixerProfile.RequestedRateHz);
            Assert.Equal(22_047u, runtime.MixerProfile.AiFrequencyReturnHz);

            var waveSources = N64SoundToolsInputResolver.SelectCarvedPair(assets);
            var bank = N64SoundToolsBank.Parse(waveSources.PointerData, waveSources.WaveData);
            var fxSources = N64SoundToolsFxInputResolver.SelectCarvedSources(assets);
            Assert.True(waveSources.PointerData.AsSpan().SequenceEqual(fxSources.PointerData));
            Assert.Equal(expected.Waves, bank.PointerBank.Waves.Count);
            Assert.Equal(expected.StoredLoops,
                bank.PointerBank.Waves.Count(static wave => wave.Loop != null));
            Assert.All(bank.PointerBank.Waves.Where(static wave => wave.Loop != null),
                static wave => Assert.Equal(uint.MaxValue, wave.Loop!.CountRaw));
            Assert.All(bank.PointerBank.FineTuneCells,
                static fineTune => Assert.Equal(0, fineTune.FineTuneCents));
            Assert.Equal(expected.Effects, fxSources.FxBank.EffectCount);

            var playbacks = Enumerable.Range(0, fxSources.FxBank.EffectCount)
                .Select(index => N64SoundToolsEffectPlaybackResolver.Resolve(
                    fxSources.FxBank,
                    bank.PointerBank,
                    index,
                    runtime.MixerProfile.AiFrequencyReturnHz))
                .ToArray();
            Assert.All(playbacks, static playback =>
            {
                Assert.False(playback.VelocitySilencedByPitchLimit);
                Assert.InRange(playback.RuntimePitchRatio, float.Epsilon, 2.0f);
                Assert.True(playback.NearestWavRateHz > 0);
                Assert.True(Math.Abs(playback.WavRateRepresentationErrorHz) <= 0.5d);
                Assert.NotEqual(N64SoundToolsEffectPlaybackResolver.FiniteStoredLoopMode,
                    playback.StoredLoopMode);
            });
            Assert.Equal(expected.PitchHistogram,
                Histogram(playbacks.Select(static playback => playback.StaticPitchSemitones)));
            Assert.Equal(expected.RateHistogram,
                Histogram(playbacks.Select(static playback => playback.NearestWavRateHz)));
            var looped = playbacks.Where(static playback => playback.PointerWave.Loop != null).ToArray();
            Assert.Equal(expected.LoopedEffects, looped.Length);
            Assert.Equal(expected.DistinctReferencedLoopWaves,
                looped.Select(static playback => playback.PointerWaveIndex).Distinct().Count());

            var firstLoop = playbacks[expected.SelectedEffect];
            Assert.Equal(expected.SelectedPointerWave, firstLoop.PointerWaveIndex);
            Assert.Equal(expected.LoopStart, firstLoop.PointerWave.Loop!.Start);
            Assert.Equal(expected.LoopEndExclusive, firstLoop.PointerWave.Loop.End);
            Assert.Equal(expected.WavRate, firstLoop.NearestWavRateHz);

            var cueBanks = N64SfxInspectCommand.SelectCarvedBanks(assets);
            var cues = cueBanks.SelectMany(static source => source.Bank.Records).ToArray();
            if (expected.ResolvedCueTargets == 0 && expected.ExplicitNoTargetCues == 0 &&
                expected.DynamicCueTargets == 0)
            {
                Assert.False(N64CompiledSfxAliasMapResolver.TryResolve(
                    boot, fxSources.FxBank.EffectCount, out var absentMap));
                Assert.Null(absentMap);
            }
            else
            {
                Assert.True(N64CompiledSfxAliasMapResolver.TryResolve(
                    boot, fxSources.FxBank.EffectCount, out var map));
                Assert.NotNull(map);
                Assert.Equal(
                    expected.Rom switch
                    {
                        "Tony Hawk's Pro Skater 2 (USA).z64" =>
                            "71D5DB520DC4985DCA0F775B3DFB035B7B02B4F077F52356792BA0CCD38E6C42",
                        "Tony Hawk's Pro Skater 3 (USA).z64" =>
                            "0E13279B2E559FD1EA027CC8B6E289E0B5FAF3190777B2897E8BCDAFAFDB2378",
                        _ => "871E6AF76CEAAB13E49DA0B826C8890DC48353E4FC7870BF4D3AE9CFF81912B5"
                    },
                    map!.LookupRoutineSha256);
                Assert.Equal(
                    expected.Rom switch
                    {
                        "Tony Hawk's Pro Skater 2 (USA).z64" =>
                            "C948CEC93EE6EA776E5B397E7A6C4CB1069F7BE2CD45E7C67502ECF6AFAE20AB",
                        "Tony Hawk's Pro Skater 3 (USA).z64" =>
                            "C7D992C17AD48D77A51BED25AFC90A9EAECD95443095C4E4AA682B3FE00F26FE",
                        _ => "56102795512EC3FE2D1375CCFD5CEBD93A69CCC6156E23900D6223EA84235FE7"
                    },
                    map.TableSha256);
                var expectedCueAliasMask = expected.Rom == "Spider-Man (USA).z64"
                    ? uint.MaxValue
                    : ushort.MaxValue;
                Assert.Equal(expectedCueAliasMask, map.CueAliasMask);
                if (expectedCueAliasMask == ushort.MaxValue)
                {
                    Assert.All(cues, static cue =>
                        Assert.Equal(0u, cue.AliasRaw & 0xFFFF0000u));
                }
                if (expected.Rom == "Tony Hawk's Pro Skater 2 (USA).z64")
                    AssertThps2RuntimeCueRules(map, cueBanks);
                else if (expected.Rom == "Tony Hawk's Pro Skater 3 (USA).z64")
                    AssertThps3NoTargetCells(map);
                else
                    AssertSpiderManPackedAliasMap(map);
                var resolutions = cueBanks.SelectMany(source =>
                    source.Bank.Records.Select(record => map!.Resolve(
                        record.AliasRaw,
                        source.Source,
                        source.Bank.SerializedSha256))).ToArray();
                Assert.All(resolutions, resolution =>
                    Assert.Equal(resolution.Alias & map.CueAliasMask, resolution.LookupAlias));
                Assert.Equal(expected.ResolvedCueTargets, resolutions.Count(static resolution =>
                    resolution.Status == N64CompiledSfxAliasMapResolver.ResolvedStatus));
                Assert.Equal(expected.ExplicitNoTargetCues, resolutions.Count(static resolution =>
                    resolution.Status == N64CompiledSfxAliasMapResolver.ExplicitlyUnmappedStatus));
                Assert.Equal(expected.DynamicCueTargets, resolutions.Count(static resolution =>
                    resolution.Status == N64CompiledSfxAliasMapResolver.DynamicOverrideStatus));
                Assert.DoesNotContain(resolutions, static resolution =>
                    resolution.Status == N64CompiledSfxAliasMapResolver.OutsidePinnedTableStatus);
                Assert.Equal(
                    expected.Rom switch
                    {
                        "Tony Hawk's Pro Skater 2 (USA).z64" =>
                            "0000:410,0400:11,0800:145,1000:3,2000:14,4000:6,8000:33,NO_TARGET:34",
                        "Tony Hawk's Pro Skater 3 (USA).z64" =>
                            "0000:321,0400:5,0800:174,2000:6,4000:4,8000:32,NO_TARGET:30",
                        _ => "0000:1534,20000:102,40000:54,80000:6,NO_TARGET:233"
                    },
                    Histogram(resolutions
                        .Where(static resolution =>
                            resolution.Status != N64CompiledSfxAliasMapResolver.DynamicOverrideStatus)
                        .Select(resolution => resolution.CompiledTargetRaw == map.ExplicitNoTargetRaw
                            ? "NO_TARGET"
                            : $"{resolution.RoutingFlagsRaw!.Value:X4}")));
            }

            var output = Path.Combine(temp.Path, $"{expected.SelectedEffect}-{expected.Rom}.wav");
            Assert.Equal(0, N64AudioDecodeCommand.ExecuteSelection(
                romPath!, wavePath: null, waveIndex: null,
                effectIndex: expected.SelectedEffect, sampleRate: null,
                output, TestContext.Current.CancellationToken));
            AssertSelectedWav(
                output,
                expected.WavRate,
                expected.LoopStart,
                expected.LoopEndExclusive - 1,
                expected.DecodedSamples,
                expected.PcmSha256);

            totalAssets += assets.Count;
            totalWaves += bank.PointerBank.Waves.Count;
            totalStoredLoops += bank.PointerBank.Waves.Count(static wave => wave.Loop != null);
            totalEffects += playbacks.Length;
            totalLoopedEffects += looped.Length;
            totalDistinctReferencedLoopWaves += looped
                .Select(static playback => playback.PointerWaveIndex).Distinct().Count();
            totalCueBanks += cueBanks.Count;
            totalCues += cues.Length;
        }

        Assert.Equal(13_864, totalAssets);
        Assert.Equal(1_775, totalWaves);
        Assert.Equal(320, totalStoredLoops);
        Assert.Equal(1_680, totalEffects);
        Assert.Equal(359, totalLoopedEffects);
        Assert.Equal(314, totalDistinctReferencedLoopWaves);
        Assert.Equal(83, totalCueBanks);
        Assert.Equal(3_172, totalCues);
    }

    private static void AssertThps2RuntimeCueRules(
        N64CompiledSfxAliasMap map,
        IReadOnlyList<N64SfxCueBankSource> cueBanks)
    {
        Assert.Equal(8, map.PinnedEvidenceRanges.Count);
        var cueParser = Assert.Single(map.PinnedEvidenceRanges,
            static evidence => evidence.Purpose.StartsWith("cue parser:", StringComparison.Ordinal));
        Assert.Equal(0x12EAC, cueParser.Offset);
        Assert.Equal(0x158, cueParser.Length);
        Assert.Equal("B5FE669E6E7F77FF72E1369AA23DD0F632D0D702B7F9A54C75B2AB0430F2E72A",
            cueParser.Sha256);
        var ownerDescriptor = Assert.Single(map.PinnedEvidenceRanges,
            static evidence => evidence.Purpose ==
                "level descriptor selector table used by runtime state branches");
        Assert.Equal(N64CompiledSfxAliasMapResolver.DataEvidenceKind, ownerDescriptor.Kind);
        Assert.Equal(0xC6550, ownerDescriptor.Offset);
        Assert.Equal(0xA0, ownerDescriptor.Length);
        Assert.Equal(
            "696845B42634B95F560E983276B2D8CF481B3B3C1850396E8057D6BBA156C14C",
            ownerDescriptor.Sha256);
        Assert.NotNull(map.CueOwnerLayout);
        var ownerLayout = map.CueOwnerLayout!;
        Assert.Equal(0x800E4A84u, ownerLayout.OwnerIndexRamAddress);
        Assert.Equal(0xC6550, ownerLayout.DescriptorTableOffset);
        Assert.Equal(0x800DD070u, ownerLayout.DescriptorTableRamAddress);
        Assert.Equal(0x10, ownerLayout.DescriptorEntryStride);
        Assert.Equal(0x800F0E80u, ownerLayout.ActiveRecordBaseRamAddress);
        Assert.Equal(0x180, ownerLayout.ActiveRecordStride);
        Assert.Equal(0x44, ownerLayout.ActiveRecordFieldOffset);

        // A cue-bank identity does not establish the mutable live owner. Keep
        // every executable branch dynamic even when its source path and bytes
        // match this corpus exactly.
        Assert.Empty(map.ContextualResolutions);
        Assert.Equal(
            new uint[] { 0xF4, 0x13C, 0x156, 0x157, 0x158 },
            map.DynamicOverrideRules.Keys.Order().ToArray());
        var dynamicCues = cueBanks
            .SelectMany(static bank => bank.Bank.Records.Select(record => (bank, record)))
            .Where(item => map.DynamicOverrideAliases.Contains(item.record.AliasRaw))
            .ToArray();
        Assert.Equal(15, dynamicCues.Length);
        Assert.Equal(3, dynamicCues.Count(static item => item.record.AliasRaw == 0x158));
        Assert.All(dynamicCues, item =>
        {
            var resolution = map.Resolve(
                item.record.AliasRaw,
                item.bank.Source,
                item.bank.Bank.SerializedSha256);
            Assert.Equal(N64CompiledSfxAliasMapResolver.DynamicOverrideStatus, resolution.Status);
            Assert.Equal(N64CompiledSfxAliasMapResolver.ExecutableStateBranchBasis,
                resolution.ResolutionBasis);
        });
        Assert.Contains(map.DynamicOverrideRules[0x158].Cases,
            static branch => branch.CompiledTargetRaw == null);
        Assert.Equal(0x0023u, map.StaticTableRaw[0xF4]);
        Assert.All(new[] { 0x13C, 0x156, 0x157, 0x158 },
            alias => Assert.Equal((uint)ushort.MaxValue, map.StaticTableRaw[alias]));
    }

    private static void AssertThps3NoTargetCells(N64CompiledSfxAliasMap map)
    {
        Assert.Null(map.CueOwnerLayout);
        Assert.Equal(7, map.PinnedEvidenceRanges.Count);
        var cueParser = Assert.Single(map.PinnedEvidenceRanges,
            static evidence => evidence.Purpose.StartsWith("cue parser:", StringComparison.Ordinal));
        Assert.Equal(0x11A48, cueParser.Offset);
        Assert.Equal(0x158, cueParser.Length);
        Assert.Equal("E8D8371CD6FD58A2A86EE843B6264734D0BC347F897BA993E5CA8AAA5FF6A403",
            cueParser.Sha256);
        Assert.Empty(map.DynamicOverrideRules);
        Assert.Empty(map.ContextualResolutions);
        Assert.All(new[] { 0x13C, 0x156, 0x157, 0x158 },
            alias => Assert.Equal((uint)ushort.MaxValue, map.StaticTableRaw[alias]));
    }

    private static void AssertSpiderManPackedAliasMap(N64CompiledSfxAliasMap map)
    {
        Assert.Null(map.CueOwnerLayout);
        Assert.Equal(4, map.PinnedEvidenceRanges.Count);
        var directHelper = Assert.Single(map.PinnedEvidenceRanges,
            static evidence => evidence.Purpose.StartsWith("direct full-u32", StringComparison.Ordinal));
        Assert.Equal(0x1A2F8, directHelper.Offset);
        Assert.Equal(0xE0, directHelper.Length);
        Assert.Equal("5E3B08A20A3FACA95B7D6C99E7649B5917AD1B14CEEC5725230C5044893C9AE1",
            directHelper.Sha256);
        Assert.Equal(sizeof(uint), map.TableEntrySize);
        Assert.Equal(482, map.StaticTableRaw.Count);
        Assert.Equal(0x0000_0FA0u, map.StaticTableRaw[0]);
        Assert.Equal(0x0002_0052u, map.StaticTableRaw[1]);
        Assert.Equal(0x0000_0065u, map.StaticTableRaw[2]);
        Assert.Equal(0x0000_0FA0u, map.StaticTableRaw[4]);
        Assert.Equal(0x0002_0081u, map.StaticTableRaw[49]);
        Assert.Equal(0x0002_0144u, map.StaticTableRaw[50]);
        Assert.Equal(0x0000_0011u, map.StaticTableRaw[481]);
        var mapped = map.StaticTableRaw.Where(raw => raw != map.ExplicitNoTargetRaw).ToArray();
        Assert.Equal(326, mapped.Length);
        Assert.Equal(156, map.StaticTableRaw.Count(raw => raw == map.ExplicitNoTargetRaw));
        Assert.Equal(1, mapped.Min(raw => map.DecodeEffectIndex(raw)!.Value));
        Assert.Equal(363, mapped.Max(raw => map.DecodeEffectIndex(raw)!.Value));
        Assert.Equal(260, mapped.Select(raw => map.DecodeEffectIndex(raw)!.Value).Distinct().Count());
        Assert.Equal("00000000:277,00020000:31,00040000:16,00080000:2",
            Histogram(mapped.Select(raw => $"{map.DecodeRoutingFlags(raw)!.Value:X8}")));
    }

    private static byte[] BuildEvent(byte localWave, byte note) =>
    [
        0x81, localWave,
        0x84, 1, 0x7F, 1, 0x7F, 1, 0x7F, 0x10,
        0x9C, 0x7F,
        0xA6, 0x7F,
        note, 1,
        0x80
    ];

    private static string Histogram<T>(IEnumerable<T> values) where T : notnull =>
        string.Join(",", values
            .GroupBy(static value => value)
            .OrderBy(static group => group.Key)
            .Select(static group => string.Format(
                CultureInfo.InvariantCulture,
                "{0}:{1}",
                group.Key,
                group.Count())));

    private static void AssertSelectedWav(
        string path,
        int expectedRate,
        uint expectedLoopStart,
        uint expectedLoopEndInclusive,
        int expectedDecodedSamples,
        string expectedPcmSha256)
    {
        var wav = File.ReadAllBytes(path);
        Assert.Equal("RIFF"u8.ToArray(), wav[..4]);
        Assert.Equal(wav.Length - 8, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(4)));
        Assert.Equal(expectedRate, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        var dataSize = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40));
        Assert.Equal(expectedDecodedSamples * sizeof(short), dataSize);
        Assert.Equal(expectedPcmSha256,
            Convert.ToHexString(SHA256.HashData(wav.AsSpan(44, dataSize))));
        var samplerOffset = checked(44 + dataSize);
        Assert.Equal("smpl"u8.ToArray(), wav[samplerOffset..(samplerOffset + 4)]);
        Assert.Equal(60u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(samplerOffset + 4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(samplerOffset + 36)));
        Assert.Equal(expectedLoopStart,
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(samplerOffset + 52)));
        Assert.Equal(expectedLoopEndInclusive,
            BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(samplerOffset + 56)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(samplerOffset + 64)));
        Assert.Equal(samplerOffset + 68, wav.Length);
    }

    private sealed record CorpusExpected(
        string Build,
        string Rom,
        int Assets,
        int Waves,
        int StoredLoops,
        int Effects,
        int LoopedEffects,
        int DistinctReferencedLoopWaves,
        string PitchHistogram,
        string RateHistogram,
        int SelectedEffect,
        int SelectedPointerWave,
        uint LoopStart,
        uint LoopEndExclusive,
        int WavRate,
        int DecodedSamples,
        string PcmSha256,
        int ResolvedCueTargets,
        int ExplicitNoTargetCues,
        int DynamicCueTargets);

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "nmt-n64-effect-playback-" + Guid.NewGuid().ToString("N"));
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
