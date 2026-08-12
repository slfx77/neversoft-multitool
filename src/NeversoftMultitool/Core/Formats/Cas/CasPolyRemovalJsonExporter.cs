using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Cas;

/// <summary>Writes deterministic schema-v1 inspection JSON for CAS polygon-removal metadata.</summary>
public static class CasPolyRemovalJsonExporter
{
    public const string SchemaName = "neversoft.cas.polyRemoval";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static void Write(string outputPath, string sourceFile, CasPolyRemovalDocument document)
    {
        // Fully materialize the manifest before creating or truncating the destination.
        var json = Serialize(sourceFile, document);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, json);
    }

    public static string Serialize(string sourceFile, CasPolyRemovalDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            Platform = document.Platform switch
            {
                CasPolyRemovalPlatform.Ps2 => "ps2",
                CasPolyRemovalPlatform.Xbox => "xbox",
                _ => throw new InvalidDataException($"Unsupported CAS platform {document.Platform}")
            },
            ByteOrder = "littleEndian",
            SerializedSize = document.SerializedSize,
            SerializedSha256 = document.SerializedSha256,
            FormatVersion = document.Version,
            RemovalMask = ToHex(document.RemovalMask),
            EntryCount = document.Entries.Length,
            GeometryApplicationStatus = "notApplied",
            Entries = document.Entries.Select(entry => ToManifestEntry(document.Platform, entry)).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static object ToManifestEntry(CasPolyRemovalPlatform platform, CasPolyRemovalEntry entry)
    {
        return (platform, entry) switch
        {
            (CasPolyRemovalPlatform.Ps2, CasPs2PolyRemovalEntry ps2) => new Ps2ManifestEntry
            {
                Mask = ToHex(ps2.Mask),
                VertexReference = ps2.VertexReference
            },
            (CasPolyRemovalPlatform.Xbox, CasXboxPolyRemovalEntry xbox) => new XboxManifestEntry
            {
                Mask = ToHex(xbox.Mask),
                Data0 = ToHex(xbox.Data0),
                Data1 = ToHex(xbox.Data1),
                MeshLoadOrder = xbox.MeshLoadOrder,
                VertexIndices = xbox.VertexIndices
            },
            _ => throw new InvalidDataException(
                $"CAS {platform} document contains an incompatible {entry.GetType().Name} record")
        };
    }

    private static string ToHex(uint value)
    {
        return $"0x{value:X8}";
    }

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required string Platform { get; init; }
        public required string ByteOrder { get; init; }
        public required int SerializedSize { get; init; }
        public required string SerializedSha256 { get; init; }
        public required uint FormatVersion { get; init; }
        public required string RemovalMask { get; init; }
        public required int EntryCount { get; init; }
        public required string GeometryApplicationStatus { get; init; }
        public required object[] Entries { get; init; }
    }

    private sealed class Ps2ManifestEntry
    {
        public required string Mask { get; init; }
        public required int VertexReference { get; init; }
    }

    private sealed class XboxManifestEntry
    {
        public required string Mask { get; init; }
        public required string Data0 { get; init; }
        public required string Data1 { get; init; }
        public required uint MeshLoadOrder { get; init; }
        public required ushort[] VertexIndices { get; init; }
    }
}
