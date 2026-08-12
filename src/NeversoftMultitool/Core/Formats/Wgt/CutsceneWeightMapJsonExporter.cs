using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Wgt;

/// <summary>Writes deterministic schema-v1 inspection JSON for compiled WGT v1 metadata.</summary>
public static class CutsceneWeightMapJsonExporter
{
    public const string SchemaName = "neversoft.wgt.meshScaling";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static void Write(
        string outputPath,
        string sourceFile,
        CutsceneWeightMapDocument document)
    {
        // Fully materialize the manifest before creating or truncating the destination.
        var json = Serialize(sourceFile, document);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, json);
    }

    public static string Serialize(string sourceFile, CutsceneWeightMapDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(document.Vertices);

        if (document.Version != 1)
            throw new InvalidDataException($"Unsupported WGT version {document.Version}; expected exactly version 1");

        var platform = document.Platform switch
        {
            CutsceneWeightMapPlatform.Ps2 => "ps2",
            CutsceneWeightMapPlatform.Xbox => "xbox",
            _ => throw new InvalidDataException($"Unsupported WGT platform {document.Platform}")
        };

        var vertices = new VertexManifest[document.Vertices.Length];
        for (var i = 0; i < vertices.Length; i++)
        {
            var vertex = document.Vertices[i]
                ?? throw new InvalidDataException($"WGT document contains a null vertex at index {i}");
            if (!float.IsFinite(vertex.Weight0)
                || !float.IsFinite(vertex.Weight1)
                || !float.IsFinite(vertex.Weight2))
            {
                throw new InvalidDataException($"WGT vertex {i} contains a non-finite mesh-scaling weight");
            }

            vertices[i] = new VertexManifest
            {
                Weights = vertex.Weights,
                BoneIndices = vertex.BoneIndices
            };
        }

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            Platform = platform,
            ByteOrder = "littleEndian",
            SerializedSize = document.SerializedSize,
            SerializedSha256 = document.SerializedSha256,
            FormatVersion = document.Version,
            VertexCount = vertices.Length,
            GeometryApplicationStatus = "notApplied",
            Vertices = vertices
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
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
        public required int VertexCount { get; init; }
        public required string GeometryApplicationStatus { get; init; }
        public required VertexManifest[] Vertices { get; init; }
    }

    private sealed class VertexManifest
    {
        public required float[] Weights { get; init; }
        public required sbyte[] BoneIndices { get; init; }
    }
}
