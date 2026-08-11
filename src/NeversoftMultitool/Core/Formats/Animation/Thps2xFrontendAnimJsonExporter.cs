using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Writes a deterministic inspection manifest for THPS2X frontend timelines.</summary>
internal static class Thps2XFrontendAnimJsonExporter
{
    internal const string SchemaName = "neversoft.thps2x.frontendAnim";
    internal const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static void Write(string outputPath, string sourceFile, Thps2XFrontendAnim animation)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, Serialize(sourceFile, animation));
    }

    internal static string Serialize(string sourceFile, Thps2XFrontendAnim animation)
    {
        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            FormatVersion = animation.Version,
            Duration = animation.Duration,
            SerializedSize = animation.SerializedSize,
            RootCount = animation.Roots.Length,
            NodeCount = animation.NodeCount,
            KeyCount = animation.KeyCount,
            Roots = animation.Roots.Select(ToManifestNode).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static ManifestNode ToManifestNode(Thps2XFrontendAnimNode node)
    {
        return new ManifestNode
        {
            SerializedOffset = node.SerializedOffset,
            SerializedSize = node.SerializedSize,
            Name = node.Name,
            BaseValues = node.BaseValues,
            RawUnknown32 = node.RawUnknown32,
            Keys = node.Keys.Select(static key => new ManifestKey
            {
                SerializedOffset = key.SerializedOffset,
                Values = key.Values,
                RawUnknown16 = key.RawUnknown16,
                TrailingValue = key.TrailingValue
            }).ToArray(),
            Children = node.Children.Select(ToManifestNode).ToArray(),
            ClosingName = node.ClosingName
        };
    }

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required uint FormatVersion { get; init; }
        public required float Duration { get; init; }
        public required int SerializedSize { get; init; }
        public required int RootCount { get; init; }
        public required int NodeCount { get; init; }
        public required int KeyCount { get; init; }
        public required ManifestNode[] Roots { get; init; }
    }

    private sealed class ManifestNode
    {
        public required int SerializedOffset { get; init; }
        public required int SerializedSize { get; init; }
        public required string Name { get; init; }
        public required float[] BaseValues { get; init; }
        public required uint RawUnknown32 { get; init; }
        public required ManifestKey[] Keys { get; init; }
        public required ManifestNode[] Children { get; init; }
        public required string ClosingName { get; init; }
    }

    private sealed class ManifestKey
    {
        public required int SerializedOffset { get; init; }
        public required float[] Values { get; init; }
        public required ushort RawUnknown16 { get; init; }
        public required float TrailingValue { get; init; }
    }
}
