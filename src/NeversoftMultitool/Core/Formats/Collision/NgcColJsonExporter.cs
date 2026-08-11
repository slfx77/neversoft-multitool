using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Collision;

/// <summary>Writes a deterministic structural inspection manifest for a GameCube collision file.</summary>
public static class NgcColJsonExporter
{
    public const string SchemaName = "neversoft.ngc.col";
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static void Write(string outputPath, string sourceFile, NgcColScene scene)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, Serialize(sourceFile, scene));
    }

    public static string Serialize(string sourceFile, NgcColScene scene)
    {
        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            FormatVersion = scene.Version,
            VertexStorage =
                "none: the engine binds collision vertices to the render scene's vertex pool at load",
            SuperSectorRows = scene.SuperSectorRows,
            SuperSectorCols = scene.SuperSectorCols,
            SceneBoundsMin = ToArray(scene.SceneBoundsMin),
            SceneBoundsMax = ToArray(scene.SceneBoundsMax),
            TotalVerts = scene.TotalVerts,
            TotalFaces = scene.TotalFaces,
            PoolElementCount = scene.PoolElementCount,
            FaceIndicesObjectContained = scene.FaceIndicesObjectContained,
            CornerIntensitiesUniform = scene.CornerIntensitiesUniform,
            CornerIntensitiesHex = scene.CornerIntensitiesUniform
                ? null
                : Convert.ToHexString(scene.CornerIntensities),
            Objects = scene.Objects.Select(ToManifestObject).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static ManifestObject ToManifestObject(NgcColObject obj)
    {
        return new ManifestObject
        {
            Checksum = $"0x{obj.Checksum:X8}",
            Name = Core.QbKey.QbKey.TryResolve(obj.Checksum),
            NumVerts = obj.NumVerts,
            NumFaces = obj.Faces.Length,
            FirstVertIndex = obj.FirstVertIndex,
            FirstFaceIndex = obj.FirstFaceIndex,
            BBoxMin = ToArray(obj.BBoxMin),
            BBoxMax = ToArray(obj.BBoxMax),
            Faces = obj.Faces.Select(static face => new ManifestFace
            {
                Flags = face.Flags,
                Terrain = face.TerrainType,
                Verts = [face.V0, face.V1, face.V2]
            }).ToArray(),
            Bsp = ToManifestNode(obj.BspRoot)
        };
    }

    private static ManifestBspNode ToManifestNode(NgcColBspNode node)
    {
        if (node.IsLeaf)
        {
            return new ManifestBspNode
            {
                FaceIndices = node.LeafFaceIndices
            };
        }

        return new ManifestBspNode
        {
            Axis = "XYZ"[node.Axis].ToString(),
            SplitPoint = node.SplitPoint,
            Less = ToManifestNode(node.Less!),
            Greater = ToManifestNode(node.Greater!)
        };
    }

    private static float[] ToArray(Vector4 value)
    {
        return [value.X, value.Y, value.Z, value.W];
    }

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required int FormatVersion { get; init; }
        public required string VertexStorage { get; init; }
        public required int SuperSectorRows { get; init; }
        public required int SuperSectorCols { get; init; }
        public required float[] SceneBoundsMin { get; init; }
        public required float[] SceneBoundsMax { get; init; }
        public required int TotalVerts { get; init; }
        public required int TotalFaces { get; init; }
        public required int PoolElementCount { get; init; }
        public required bool FaceIndicesObjectContained { get; init; }
        public required bool CornerIntensitiesUniform { get; init; }
        public string? CornerIntensitiesHex { get; init; }
        public required ManifestObject[] Objects { get; init; }
    }

    private sealed class ManifestObject
    {
        public required string Checksum { get; init; }
        public string? Name { get; init; }
        public required int NumVerts { get; init; }
        public required int NumFaces { get; init; }
        public required int FirstVertIndex { get; init; }
        public required int FirstFaceIndex { get; init; }
        public required float[] BBoxMin { get; init; }
        public required float[] BBoxMax { get; init; }
        public required ManifestFace[] Faces { get; init; }
        public required ManifestBspNode Bsp { get; init; }
    }

    private sealed class ManifestFace
    {
        public required ushort Flags { get; init; }
        public required ushort Terrain { get; init; }
        public required ushort[] Verts { get; init; }
    }

    private sealed class ManifestBspNode
    {
        public string? Axis { get; init; }
        public float? SplitPoint { get; init; }
        public ManifestBspNode? Less { get; init; }
        public ManifestBspNode? Greater { get; init; }
        public ushort[]? FaceIndices { get; init; }
    }
}
