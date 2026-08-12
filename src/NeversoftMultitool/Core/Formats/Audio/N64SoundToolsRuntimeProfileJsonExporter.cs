using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Audio;

/// <summary>Writes the separate ROM-runtime profile manifest.</summary>
internal static class N64SoundToolsRuntimeProfileJsonExporter
{
    internal const string SchemaName = "neversoft.n64.soundToolsRuntimeProfile";
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(
        string outputPath,
        string inputSource,
        N64SoundToolsRuntimeProfile profile)
    {
        var json = Serialize(inputSource, profile);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(outputPath, json);
    }

    internal static string Serialize(
        string inputSource,
        N64SoundToolsRuntimeProfile profile)
    {
        ArgumentException.ThrowIfNullOrEmpty(inputSource);
        ArgumentNullException.ThrowIfNull(profile);

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Format = "N64 Sound Tools ROM runtime profile",
            InputSource = inputSource,
            ProfileStatus = "resolved",
            DetectionBasis = N64SoundToolsRuntimeProfileResolver.DetectionBasis,
            BootSource = N64SoundToolsRuntimeProfileResolver.BootSource,
            BootSha256 = profile.BootSha256,
            RomCountryCodeRaw = profile.RomCountryCodeRaw,
            RomCountryCodeOffset = N64SoundToolsRuntimeProfileResolver.CountryCodeRomOffset,
            VideoClockRomOffset = profile.VideoClockRomOffset,
            AiRoutineSource = "rawRom",
            AiRoutineRomOffset = profile.AiRoutineRomOffset,
            AiRoutineLength = profile.AiRoutineLength,
            AiRoutineSha256 = profile.AiRoutineSha256,
            MixerProfile = profile.MixerProfile,
            PerWaveRateStatus = "unresolved",
            CueMappingStatus = "unresolved",
            PitchApplicationStatus = "notApplied",
            LoopSchedulingStatus = "notApplied",
            PlaybackStatus = "notExecuted"
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Format { get; init; }
        public required string InputSource { get; init; }
        public required string ProfileStatus { get; init; }
        public required string DetectionBasis { get; init; }
        public required string BootSource { get; init; }
        public required string BootSha256 { get; init; }
        public required byte RomCountryCodeRaw { get; init; }
        public required int RomCountryCodeOffset { get; init; }
        public required int VideoClockRomOffset { get; init; }
        public required string AiRoutineSource { get; init; }
        public required int AiRoutineRomOffset { get; init; }
        public required int AiRoutineLength { get; init; }
        public required string AiRoutineSha256 { get; init; }
        public required N64SoundToolsMixerProfile MixerProfile { get; init; }
        public required string PerWaveRateStatus { get; init; }
        public required string CueMappingStatus { get; init; }
        public required string PitchApplicationStatus { get; init; }
        public required string LoopSchedulingStatus { get; init; }
        public required string PlaybackStatus { get; init; }
    }
}
