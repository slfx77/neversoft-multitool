using System.Buffers.Binary;
using System.Numerics;
using NeversoftMultitool.CLI;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool.Tests.Core.Formats.Animation;

/// <summary>
///     Project 8 / Proving Ground's big-endian, section-addressed SKA revision.
///     The whole-corpus test is deliberately explicit: structural agreement
///     across every shipped file is evidence for the container/key grammar,
///     but is not treated as an oracle for skeleton binding or visual motion.
/// </summary>
public class SkaNextGenParserTests(TestPaths paths)
{
    private const string P8X360 = "Tony Hawk's Project 8 (2006-11-7, X360 - Final)";
    private const string P8Ps3 = "Tony Hawk's Project 8 (2006-10-5, PS3 - Final)";
    private const string PgX360 = "Tony Hawk's Proving Ground (2007-8-30, X360 - Final)";
    private const string PgPs3 = "Tony Hawk's Proving Ground (2007-8-31, PS3 - Final)";

    [Fact]
    public void Parse_P8Wrapper_RoutesPlatformKeysThroughSharedSkaApi()
    {
        var data = BuildP8PlatformFixture();

        Assert.True(SkaFile.IsSkaFile(data));
        var probe = Assert.IsType<SkaProbeResult>(SkaFile.TryProbe(data));
        Assert.Equal(1, probe.BoneCount);
        Assert.Equal(1, probe.Duration);

        var animation = SkaFile.Parse(data);
        Assert.Equal(0x28u, animation.Version);
        Assert.True(animation.IsNextGenWrappedFormat);
        Assert.True(animation.IsThawFormat);
        Assert.Single(animation.BoneTracks);
        Assert.Equal(Quaternion.Identity, Assert.Single(animation.BoneTracks[0].RotationKeys).Rotation);
        Assert.Equal(new Vector3(1, 2, 3),
            Assert.Single(animation.BoneTracks[0].TranslationKeys).Translation);
    }

    [Fact]
    public void Parse_PgWrapper_DecodesSingleFrameVectorVariant()
    {
        var data = BuildPgSingleFrameFixture();

        Assert.True(SkaFile.IsSkaFile(data));
        var animation = SkaFile.Parse(data);
        Assert.Equal(0x48u, animation.Version);
        Assert.Equal(Quaternion.Identity, Assert.Single(animation.BoneTracks[0].RotationKeys).Rotation);
        Assert.Equal(new Vector3(1, 2, 3),
            Assert.Single(animation.BoneTracks[0].TranslationKeys).Translation);
    }

    [Fact]
    public void Probe_RejectsMalformedNextGenWrapperAndSectionTable()
    {
        var mutations = new Action<byte[]>[]
        {
            data => WriteU32(data, 8, (uint)data.Length + 4),
            data => WriteU32(data, 12, 0x24),
            data => WriteU32(data, 0x20, 0x30),
            data => WriteU32(data, 0x38, 0x51),
            data => WriteU32(data, 0x10, (uint)data.Length)
        };

        foreach (var mutate in mutations)
        {
            var data = BuildP8PlatformFixture();
            mutate(data);
            Assert.False(SkaFile.IsSkaFile(data));
            Assert.Null(SkaFile.TryProbe(data));
            Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
        }
    }

    [Fact]
    public void Parse_PgTranslationPrefix_FailsClosedOnUnknownMarker()
    {
        var data = BuildPgCompressedPrefixFixture();
        Assert.Empty(SkaFile.Parse(data).BoneTracks[0].TranslationKeys);

        data[0x74] ^= 1;
        Assert.True(SkaFile.IsSkaFile(data));
        Assert.Throws<InvalidDataException>(() => SkaFile.Parse(data));
    }

    [CorpusFact]
    public void Parse_AllP8X360NextGenSka_FullCorpusSweep() =>
        SweepBuild(P8X360, "*.ska.xen", 9467, 0x28);

    [CorpusFact]
    public void Parse_AllP8Ps3NextGenSka_FullCorpusSweep() =>
        SweepBuild(P8Ps3, "*.ska.ps3", 9467, 0x28);

    [CorpusFact]
    public void Parse_AllPgX360NextGenSka_FullCorpusSweep() =>
        SweepBuild(PgX360, "*.ska.xen", 8641, 0x48);

    [CorpusFact]
    public void Parse_AllPgPs3NextGenSka_FullCorpusSweep() =>
        SweepBuild(PgPs3, "*.ska.ps3", 17074, 0x48);

    [CorpusFact]
    public void Parse_PinnedCrossPlatformPairs_AreByteAndDecodeIdentical()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        AssertCrossPlatformPair(
            P8X360,
            Path.Combine("DATA", "COMPRESSED", "CUTSCENES", "bam_mugging_cam0.pak",
                "cutscene_00001100.ska.xen"),
            P8Ps3,
            Path.Combine("PS3_GAME", "USRDIR", "DATA", "CUTSCENES", "bam_mugging_cam0.pak",
                "cutscene_000001E0.ska.ps3"));
        AssertCrossPlatformPair(
            PgX360,
            Path.Combine("DATA", "COMPRESSED", "CUTSCENES", "bam_mugging_cam0.pak",
                "cutscene_00001000.ska.xen"),
            PgPs3,
            Path.Combine("PS3_GAME", "USRDIR", "DATA", "COMPRESSED", "PS3", "CUTSCENES",
                "BAM_MUGGING_CAM0.PAK", "cutscene_00000100.ska.ps3"));
    }

    private void SweepBuild(
        string build,
        string pattern,
        int expectedCount,
        uint expectedHeader)
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");

        var failures = new List<string>();
        var parsed = 0;
        var files = paths.FindSampleFiles(build, pattern).ToArray();
        Assert.Equal(expectedCount, files.Length);
        var table = files.Length > 0 ? SkaCommand.FindCompressTable(files[0]) : null;
        Assert.NotNull(table);

        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (!SkaFile.IsSkaFile(data))
                    throw new InvalidDataException("IsSkaFile rejected the wrapper");

                var probe = SkaFile.TryProbe(data)
                            ?? throw new InvalidDataException("TryProbe rejected the wrapper");
                var animation = SkaFile.Parse(data, table);
                if (animation.BoneTracks.Length != probe.BoneCount)
                {
                    throw new InvalidDataException(
                        $"probe reports {probe.BoneCount} bones, parser returned " +
                        $"{animation.BoneTracks.Length}");
                }

                if (BitConverter.SingleToInt32Bits(animation.Duration) !=
                    BitConverter.SingleToInt32Bits(probe.Duration))
                    throw new InvalidDataException("probe and parser durations differ");
                if (!animation.IsNextGenWrappedFormat || !animation.IsThawFormat)
                    throw new InvalidDataException("next-gen format marker was not preserved");
                if (animation.Version != expectedHeader)
                    throw new InvalidDataException($"unexpected payload header 0x{animation.Version:X}");

                foreach (var track in animation.BoneTracks)
                {
                    ValidateRotationTrack(track.RotationKeys, animation.Duration);
                    ValidateTranslationTrack(track.TranslationKeys, animation.Duration);
                }

                for (var i = 1; i < animation.CustomKeys.Length; i++)
                {
                    if (animation.CustomKeys[i].Timestamp < animation.CustomKeys[i - 1].Timestamp)
                        throw new InvalidDataException($"custom-key timestamp regressed at key {i}");
                }

                parsed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetRelativePath(Path.Combine(paths.SampleBuildsDir!, build), file)}: {ex.Message}");
            }
        }

        Assert.True(failures.Count == 0,
            $"{build}: {failures.Count}/{files.Length} failed:\n" + string.Join("\n", failures.Take(30)));
        Assert.Equal(files.Length, parsed);
    }

    private static void ValidateRotationTrack(SkaRotationKey[] keys, float duration)
    {
        var limit = duration + 1.5f / 60f;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!float.IsFinite(key.Time) || key.Time < 0 || key.Time > limit)
                throw new InvalidDataException($"Q key {i} has invalid time {key.Time} (duration {duration})");
            if (i > 0 && key.Time < keys[i - 1].Time)
                throw new InvalidDataException($"Q key timestamp regressed at key {i}");
            if (!IsFinite(key.Rotation))
                throw new InvalidDataException($"Q key {i} is non-finite");
        }
    }

    private static void ValidateTranslationTrack(SkaTranslationKey[] keys, float duration)
    {
        var limit = duration + 1.5f / 60f;
        for (var i = 0; i < keys.Length; i++)
        {
            var key = keys[i];
            if (!float.IsFinite(key.Time) || key.Time < 0 || key.Time > limit)
                throw new InvalidDataException($"T key {i} has invalid time {key.Time} (duration {duration})");
            if (i > 0 && key.Time < keys[i - 1].Time)
                throw new InvalidDataException($"T key timestamp regressed at key {i}");
            if (!IsFinite(key.Translation))
                throw new InvalidDataException($"T key {i} is non-finite");
        }
    }

    private static bool IsFinite(Quaternion value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private void AssertCrossPlatformPair(
        string firstBuild,
        string firstRelativePath,
        string secondBuild,
        string secondRelativePath)
    {
        var first = Path.Combine(paths.SampleBuildsDir!, firstBuild, firstRelativePath);
        var second = Path.Combine(paths.SampleBuildsDir!, secondBuild, secondRelativePath);
        Assert.SkipWhen(!File.Exists(first) || !File.Exists(second), "Pinned cross-platform pair not present");

        var firstData = File.ReadAllBytes(first);
        var secondData = File.ReadAllBytes(second);
        Assert.Equal(firstData, secondData);

        var firstAnimation = SkaFile.Parse(firstData, SkaCommand.FindCompressTable(first));
        var secondAnimation = SkaFile.Parse(secondData, SkaCommand.FindCompressTable(second));
        AssertAnimationsEqual(firstAnimation, secondAnimation);
    }

    private static void AssertAnimationsEqual(SkaAnimation expected, SkaAnimation actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Flags, actual.Flags);
        Assert.Equal(expected.Duration, actual.Duration);
        Assert.Equal(expected.BoneTracks.Length, actual.BoneTracks.Length);
        Assert.Equal(expected.CustomKeys.Length, actual.CustomKeys.Length);
        for (var bone = 0; bone < expected.BoneTracks.Length; bone++)
        {
            var expectedTrack = expected.BoneTracks[bone];
            var actualTrack = actual.BoneTracks[bone];
            Assert.Equal(expectedTrack.BoneNameChecksum, actualTrack.BoneNameChecksum);
            Assert.Equal(expectedTrack.RotationKeys.Length, actualTrack.RotationKeys.Length);
            Assert.Equal(expectedTrack.TranslationKeys.Length, actualTrack.TranslationKeys.Length);
            for (var key = 0; key < expectedTrack.RotationKeys.Length; key++)
            {
                Assert.Equal(expectedTrack.RotationKeys[key].Time, actualTrack.RotationKeys[key].Time);
                Assert.Equal(expectedTrack.RotationKeys[key].Rotation, actualTrack.RotationKeys[key].Rotation);
            }

            for (var key = 0; key < expectedTrack.TranslationKeys.Length; key++)
            {
                Assert.Equal(expectedTrack.TranslationKeys[key].Time, actualTrack.TranslationKeys[key].Time);
                Assert.Equal(expectedTrack.TranslationKeys[key].Translation,
                    actualTrack.TranslationKeys[key].Translation);
            }
        }

        for (var key = 0; key < expected.CustomKeys.Length; key++)
        {
            Assert.Equal(expected.CustomKeys[key].Timestamp, actual.CustomKeys[key].Timestamp);
            Assert.Equal(expected.CustomKeys[key].Type, actual.CustomKeys[key].Type);
            Assert.Equal(expected.CustomKeys[key].Size, actual.CustomKeys[key].Size);
            Assert.Equal(expected.CustomKeys[key].Payload, actual.CustomKeys[key].Payload);
        }
    }

    private static byte[] BuildP8PlatformFixture()
    {
        var data = CreateWrapper(0x78, 0x28, 0x10000000, 1);
        WriteU16(data, 0x2E, 1);
        WriteU16(data, 0x30, 1);
        WriteU32(data, 0x34, uint.MaxValue);
        WriteU32(data, 0x38, 0x50);
        WriteU32(data, 0x3C, 0x60);
        WriteU32(data, 0x40, 0x70);
        WriteU32(data, 0x44, 0x74);
        WriteF32(data, 0x64, 1);
        WriteF32(data, 0x68, 2);
        WriteF32(data, 0x6C, 3);
        WriteU16(data, 0x70, 16);
        WriteU16(data, 0x74, 16);
        return data;
    }

    private static byte[] BuildPgSingleFrameFixture()
    {
        var data = CreateWrapper(0x98, 0x48, 0x10000040, 1);
        WriteU16(data, 0x2E, 1);
        WriteF32(data, 0x30, 1);
        WriteF32(data, 0x40, 1);
        WriteU16(data, 0x50, 1);
        WriteU32(data, 0x54, uint.MaxValue);
        WriteU32(data, 0x58, 0x70);
        WriteU32(data, 0x5C, 0x80);
        WriteU32(data, 0x60, 0x90);
        WriteU32(data, 0x64, 0x94);
        WriteF32(data, 0x7C, 1);
        WriteF32(data, 0x80, 1);
        WriteF32(data, 0x84, 2);
        WriteF32(data, 0x88, 3);
        WriteF32(data, 0x8C, 1);
        WriteU16(data, 0x90, 16);
        WriteU16(data, 0x94, 16);
        return data;
    }

    private static byte[] BuildPgCompressedPrefixFixture()
    {
        var data = CreateWrapper(0x88, 0x48, 0x00800000, 1);
        WriteF32(data, 0x30, 1);
        WriteF32(data, 0x40, 1);
        WriteU32(data, 0x54, uint.MaxValue);
        WriteU32(data, 0x58, 0x70);
        WriteU32(data, 0x5C, 0x74);
        WriteU32(data, 0x60, 0x80);
        WriteU32(data, 0x64, 0x84);
        WriteF32(data, 0x74, 2);
        WriteU16(data, 0x84, 12);
        return data;
    }

    private static byte[] CreateWrapper(
        int length,
        uint headerSize,
        uint flags,
        float duration)
    {
        var data = new byte[length];
        WriteU32(data, 4, uint.MaxValue);
        WriteU32(data, 8, (uint)length);
        WriteU32(data, 12, 0x20);
        for (var offset = 0x10; offset < 0x20; offset += 4)
            WriteU32(data, offset, uint.MaxValue);
        WriteU32(data, 0x20, headerSize);
        WriteU32(data, 0x24, flags);
        WriteF32(data, 0x28, duration);
        data[0x2D] = 1;
        return data;
    }

    private static void WriteU16(byte[] data, int offset, ushort value) =>
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);

    private static void WriteU32(byte[] data, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(offset), value);

    private static void WriteF32(byte[] data, int offset, float value) =>
        BinaryPrimitives.WriteSingleBigEndian(data.AsSpan(offset), value);
}
