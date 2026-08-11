using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Writes a stable inspection sidecar for SKA custom events. Custom keys
///     are intentionally kept out of glTF animation channels because they are
///     engine actions rather than node TRS samples.
/// </summary>
internal static class SkaCustomKeyJsonExporter
{
    internal const string SchemaName = "neversoft.ska.customKeys";
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(string outputPath, string sourceFile, SkaAnimation animation)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, Serialize(sourceFile, animation));
    }

    internal static string Serialize(string sourceFile, SkaAnimation animation)
    {
        var manifest = new CustomKeyManifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            DurationSeconds = animation.Duration,
            Keys = animation.CustomKeys.Select(ToManifestKey).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static CustomKeyManifestEntry ToManifestKey(SkaCustomKey key)
    {
        var typedPayload = key.Fov.HasValue || key.ScriptQbKey.HasValue;
        return new CustomKeyManifestEntry
        {
            Timestamp = key.Timestamp,
            Type = key.Type,
            Name = key.Name,
            Size = key.Size,
            Fov = key.Fov,
            ScriptQbKey = key.ScriptQbKey.HasValue ? $"0x{key.ScriptQbKey.Value:X8}" : null,
            ScriptName = key.ScriptQbKey.HasValue
                ? QbKey.QbKey.TryResolve(key.ScriptQbKey.Value)
                : null,
            PayloadHex = !typedPayload || key.Size != 16 ? Convert.ToHexString(key.Payload) : null
        };
    }

    private sealed class CustomKeyManifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required float DurationSeconds { get; init; }
        public required CustomKeyManifestEntry[] Keys { get; init; }
    }

    private sealed class CustomKeyManifestEntry
    {
        public required uint Timestamp { get; init; }
        public required uint Type { get; init; }
        public required string Name { get; init; }
        public required uint Size { get; init; }
        public float? Fov { get; init; }
        public string? ScriptQbKey { get; init; }
        public string? ScriptName { get; init; }
        public string? PayloadHex { get; init; }
    }
}
