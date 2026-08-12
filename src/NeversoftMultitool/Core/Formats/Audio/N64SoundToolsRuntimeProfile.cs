using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>
///     ROM-executable provenance for the global Nintendo Sound Tools mixer output.
///     This is deliberately separate from PTR/WBK wave metadata: it is not an
///     authored per-wave rate and does not select a cue, pitch, or loop schedule.
/// </summary>
internal sealed record N64SoundToolsRuntimeProfile(
    string BootSha256,
    byte RomCountryCodeRaw,
    int VideoClockRomOffset,
    string AiRoutineSha256,
    int AiRoutineRomOffset,
    int AiRoutineLength,
    N64SoundToolsMixerProfile MixerProfile);

internal sealed record N64SoundToolsMixerProfile(
    string Scope,
    string VideoStandard,
    uint VideoClockHz,
    uint RequestedRateHz,
    uint AiDacRateDivisor,
    uint AiDacRateRegisterValue,
    uint AiFrequencyReturnHz);

/// <summary>
///     Resolves only the four audited final-ROM runtime profiles. Detection is
///     fail-closed: an exact carved boot hash, NTSC country code, clock word,
///     and osAiSetFrequency routine at build-pinned raw-ROM offsets must agree.
/// </summary>
internal static class N64SoundToolsRuntimeProfileResolver
{
    internal const string DetectionBasis =
        "exactBootSha256NtscCountryClockAndAiRoutine";
    internal const string BootSource = "boot.bin";
    internal const string MixerScope = "romGlobalMixerOutput";
    internal const string VideoStandard = "NTSC";

    internal const byte NtscCountryCodeRaw = 0x45;
    internal const uint NtscVideoClockHz = 48_681_812;
    internal const uint RequestedMixerRateHz = 22_050;

    internal const string Thps1BootSha256 =
        "F18F25795709791B62CD3282798991F7AD967AADE0CCD4CA316179212732A153";
    internal const string Thps2BootSha256 =
        "C3FB620AA6FA679671EEE7A340DBD7E96ECAF3C3559B57A120561850D4476787";
    internal const string Thps3BootSha256 =
        "E0CF7950E722EEB59555EF01D027771502C739ECB6CF47ED09522128B477249B";
    internal const string SpiderManBootSha256 =
        "1D3ED3384F45ADA2EBF6CB0666DDC7FEC4C4FFDB6FDB993B8D566AA3CD4F3867";

    internal const int Thps1ClockRomOffset = 0x12708;
    internal const int Thps2ClockRomOffset = 0x12898;
    internal const int Thps3ClockRomOffset = 0x12898;
    internal const int SpiderManClockRomOffset = 0x12858;

    internal const int AiRoutineLength = 0x160;
    internal const int Thps1AiRoutineRomOffset = 0x3AB0;
    internal const int Thps2AiRoutineRomOffset = 0x3BD0;
    internal const int Thps3AiRoutineRomOffset = 0x3BD0;
    internal const int SpiderManAiRoutineRomOffset = 0x3BA0;

    internal const string Thps1AiRoutineSha256 =
        "3EB88FD7E23CC742ABDF7F354BE4C625B23644209C119411543F9F1D2B3D0727";
    internal const string Thps2And3AiRoutineSha256 =
        "95FED6D48E762F4347165599D111A1485EBE599F15482A03DDF80FC8361FBF33";
    internal const string SpiderManAiRoutineSha256 =
        "69CE8CC7382E702C0CA49F917577A5296B877378E409F3BD5786DADC1A4A78FD";

    private const uint BigEndianRomMagic = 0x80371240;
    internal const int CountryCodeRomOffset = 0x3E;

    internal static N64SoundToolsRuntimeProfile Resolve(byte[] rom, byte[] bootData)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentNullException.ThrowIfNull(bootData);

        var bootSha256 = Convert.ToHexString(SHA256.HashData(bootData));
        return ResolveForKnownBootSha(rom, bootSha256);
    }

    /// <summary>
    ///     Split from hashing so focused tests can exercise every fail-closed
    ///     ROM provenance check without manufacturing a SHA-256 preimage.
    /// </summary>
    internal static N64SoundToolsRuntimeProfile ResolveForKnownBootSha(
        byte[] rom,
        string bootSha256)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentException.ThrowIfNullOrEmpty(bootSha256);

        var evidence = bootSha256 switch
        {
            Thps1BootSha256 => new KnownBuildEvidence(
                Thps1ClockRomOffset,
                Thps1AiRoutineRomOffset,
                Thps1AiRoutineSha256),
            Thps2BootSha256 => new KnownBuildEvidence(
                Thps2ClockRomOffset,
                Thps2AiRoutineRomOffset,
                Thps2And3AiRoutineSha256),
            Thps3BootSha256 => new KnownBuildEvidence(
                Thps3ClockRomOffset,
                Thps3AiRoutineRomOffset,
                Thps2And3AiRoutineSha256),
            SpiderManBootSha256 => new KnownBuildEvidence(
                SpiderManClockRomOffset,
                SpiderManAiRoutineRomOffset,
                SpiderManAiRoutineSha256),
            _ => throw new InvalidDataException(
                $"unsupported N64 Sound Tools boot SHA-256 {bootSha256}")
        };

        return ResolveForEvidence(
            rom,
            bootSha256,
            evidence.ClockRomOffset,
            evidence.AiRoutineRomOffset,
            evidence.AiRoutineSha256);
    }

    /// <summary>
    ///     Validates one already selected evidence tuple. Kept independent of
    ///     the SHA allowlist so focused synthetic tests can exercise every
    ///     country/clock/routine mutation without a boot-hash preimage.
    /// </summary>
    internal static N64SoundToolsRuntimeProfile ResolveForEvidence(
        byte[] rom,
        string bootSha256,
        int clockRomOffset,
        int aiRoutineRomOffset,
        string aiRoutineSha256)
    {
        ArgumentNullException.ThrowIfNull(rom);
        ArgumentException.ThrowIfNullOrEmpty(bootSha256);
        ArgumentException.ThrowIfNullOrEmpty(aiRoutineSha256);

        ArgumentOutOfRangeException.ThrowIfNegative(clockRomOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(aiRoutineRomOffset);

        if (rom.Length < 4
            || BinaryPrimitives.ReadUInt32BigEndian(rom.AsSpan(0, 4)) != BigEndianRomMagic)
        {
            throw new InvalidDataException("runtime profile input is not a big-endian .z64 ROM");
        }

        if (rom.Length <= CountryCodeRomOffset)
            throw new InvalidDataException("runtime profile ROM is truncated before its country code");
        var countryCode = rom[CountryCodeRomOffset];
        if (countryCode != NtscCountryCodeRaw)
        {
            throw new InvalidDataException(
                $"known Sound Tools boot requires NTSC country code 0x{NtscCountryCodeRaw:X2}, " +
                $"found 0x{countryCode:X2}");
        }

        if (rom.Length < clockRomOffset + sizeof(uint))
        {
            throw new InvalidDataException(
                $"runtime profile ROM is truncated before pinned NTSC clock offset " +
                $"0x{clockRomOffset:X}");
        }

        var clock = BinaryPrimitives.ReadUInt32BigEndian(
            rom.AsSpan(clockRomOffset, sizeof(uint)));
        if (clock != NtscVideoClockHz)
        {
            throw new InvalidDataException(
                $"known Sound Tools boot requires NTSC clock {NtscVideoClockHz}, " +
                $"found {clock} at pinned ROM offset 0x{clockRomOffset:X}");
        }

        if (rom.Length < aiRoutineRomOffset + AiRoutineLength)
        {
            throw new InvalidDataException(
                $"runtime profile ROM is truncated before pinned osAiSetFrequency routine " +
                $"at offset 0x{aiRoutineRomOffset:X}");
        }

        var actualAiRoutineSha256 = Convert.ToHexString(SHA256.HashData(
            rom.AsSpan(aiRoutineRomOffset, AiRoutineLength)));
        if (!StringComparer.Ordinal.Equals(actualAiRoutineSha256, aiRoutineSha256))
        {
            throw new InvalidDataException(
                $"known Sound Tools boot has unexpected osAiSetFrequency routine SHA-256 " +
                $"{actualAiRoutineSha256} at pinned ROM offset 0x{aiRoutineRomOffset:X}");
        }

        return new N64SoundToolsRuntimeProfile(
            bootSha256,
            countryCode,
            clockRomOffset,
            actualAiRoutineSha256,
            aiRoutineRomOffset,
            AiRoutineLength,
            CalculateMixerProfile(clock, RequestedMixerRateHz));
    }

    /// <summary>
    ///     Mirrors libultra osAiSetFrequency for the positive in-range rate
    ///     used by these ROMs: single-precision clock/rate, +0.5 divisor
    ///     rounding, DACRATE = divisor - 1, then integer clock/divisor return.
    /// </summary>
    internal static N64SoundToolsMixerProfile CalculateMixerProfile(
        uint videoClockHz,
        uint requestedRateHz)
    {
        ArgumentOutOfRangeException.ThrowIfZero(videoClockHz);
        ArgumentOutOfRangeException.ThrowIfZero(requestedRateHz);

        var divisor = checked((uint)((float)videoClockHz / requestedRateHz + 0.5f));
        if (divisor == 0)
            throw new InvalidDataException("AI DAC-rate divisor resolved to zero");

        return new N64SoundToolsMixerProfile(
            MixerScope,
            VideoStandard,
            videoClockHz,
            requestedRateHz,
            divisor,
            divisor - 1,
            videoClockHz / divisor);
    }

    private readonly record struct KnownBuildEvidence(
        int ClockRomOffset,
        int AiRoutineRomOffset,
        string AiRoutineSha256);
}
