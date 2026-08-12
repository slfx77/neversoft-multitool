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
        // Fully materialize and validate the manifest before creating or
        // truncating the destination.
        var json = Serialize(sourceFile, scene);
        var directory = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(outputPath, json);
    }

    public static string Serialize(string sourceFile, NgcColScene scene)
    {
        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            ByteOrder = "bigEndian",
            SerializedSize = scene.SerializedSize,
            SerializedSha256 = scene.SerializedSha256,
            FormatVersion = scene.Version,
            VertexStorageStatus = "externalRenderScenePool",
            GeometryExportStatus = "unavailableWithoutProvenRenderScenePoolBinding",
            SuperSectorRows = scene.SuperSectorRows,
            SuperSectorCols = scene.SuperSectorCols,
            SceneBoundsMin = ToArray(scene.SceneBoundsMin),
            SceneBoundsMax = ToArray(scene.SceneBoundsMax),
            TotalVerts = scene.TotalVerts,
            TotalFaces = scene.TotalFaces,
            PoolElementCount = scene.PoolElementCount,
            BspNodeByteCount = scene.BspNodeByteCount,
            BspNodeSha256 = scene.BspNodeSha256,
            FaceIndexPoolByteCount = scene.PoolElementCount * 2,
            FaceIndexPoolSha256 = scene.FaceIndexPoolSha256,
            FaceIndicesWithinCumulativeDeclaredVertexRanges =
                scene.FaceIndicesWithinCumulativeDeclaredVertexRanges,
            CornerIntensitiesByteCount = scene.CornerIntensities.Length,
            CornerIntensitiesSha256 = scene.CornerIntensitiesSha256,
            CornerIntensitiesUniform = scene.CornerIntensitiesUniform,
            CornerIntensitiesHex = scene.CornerIntensitiesUniform
                ? null
                : Convert.ToHexString(scene.CornerIntensities),
            Objects = scene.Objects.Select(obj => ToManifestObject(obj, scene)).ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static ManifestObject ToManifestObject(NgcColObject obj, NgcColScene scene)
    {
        return new ManifestObject
        {
            Checksum = $"0x{obj.Checksum:X8}",
            Name = Core.QbKey.QbKey.TryResolve(obj.Checksum),
            Flags = obj.Flags,
            NumVerts = obj.NumVerts,
            NumFaces = obj.Faces.Length,
            CumulativeDeclaredVertexBase = obj.CumulativeDeclaredVertexBase,
            FirstFaceIndex = obj.FirstFaceIndex,
            UsesSmallFaces = obj.UsesSmallFaces,
            UsesFixedVertices = obj.UsesFixedVertices,
            BspNodeByteOffset = obj.BspNodeByteOffset,
            CornerIntensityByteOffset = obj.CornerIntensityByteOffset,
            BBoxMin = ToArray(obj.BBoxMin),
            BBoxMax = ToArray(obj.BBoxMax),
            Faces = obj.Faces.Select((face, localIndex) => new ManifestFace
            {
                Flags = face.Flags,
                Terrain = face.TerrainType,
                Verts = [face.V0, face.V1, face.V2],
                CornerIntensities =
                [
                    scene.CornerIntensities[(obj.FirstFaceIndex + localIndex) * 3],
                    scene.CornerIntensities[(obj.FirstFaceIndex + localIndex) * 3 + 1],
                    scene.CornerIntensities[(obj.FirstFaceIndex + localIndex) * 3 + 2]
                ]
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
                NodeByteOffset = node.NodeByteOffset,
                LeafPoolElementOffset = node.LeafPoolElementOffset,
                FaceIndices = node.LeafFaceIndices
            };
        }

        return new ManifestBspNode
        {
            NodeByteOffset = node.NodeByteOffset,
            Axis = "XYZ"[node.Axis].ToString(),
            RawSplitWord = node.RawSplitWord,
            SplitPoint = node.SplitPoint,
            LeftIsGreater = node.LeftIsGreater,
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
        public required string ByteOrder { get; init; }
        public required int SerializedSize { get; init; }
        public required string SerializedSha256 { get; init; }
        public required int FormatVersion { get; init; }
        public required string VertexStorageStatus { get; init; }
        public required string GeometryExportStatus { get; init; }
        public required int SuperSectorRows { get; init; }
        public required int SuperSectorCols { get; init; }
        public required float[] SceneBoundsMin { get; init; }
        public required float[] SceneBoundsMax { get; init; }
        public required int TotalVerts { get; init; }
        public required int TotalFaces { get; init; }
        public required int PoolElementCount { get; init; }
        public required int BspNodeByteCount { get; init; }
        public required string BspNodeSha256 { get; init; }
        public required int FaceIndexPoolByteCount { get; init; }
        public required string FaceIndexPoolSha256 { get; init; }
        public required bool FaceIndicesWithinCumulativeDeclaredVertexRanges { get; init; }
        public required int CornerIntensitiesByteCount { get; init; }
        public required string CornerIntensitiesSha256 { get; init; }
        public required bool CornerIntensitiesUniform { get; init; }
        public string? CornerIntensitiesHex { get; init; }
        public required ManifestObject[] Objects { get; init; }
    }

    private sealed class ManifestObject
    {
        public required string Checksum { get; init; }
        public string? Name { get; init; }
        public required ushort Flags { get; init; }
        public required int NumVerts { get; init; }
        public required int NumFaces { get; init; }
        public required int CumulativeDeclaredVertexBase { get; init; }
        public required int FirstFaceIndex { get; init; }
        public required bool UsesSmallFaces { get; init; }
        public required bool UsesFixedVertices { get; init; }
        public required int BspNodeByteOffset { get; init; }
        public required int CornerIntensityByteOffset { get; init; }
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
        public required byte[] CornerIntensities { get; init; }
    }

    private sealed class ManifestBspNode
    {
        public required int NodeByteOffset { get; init; }
        public string? Axis { get; init; }
        public int? RawSplitWord { get; init; }
        public float? SplitPoint { get; init; }
        public bool? LeftIsGreater { get; init; }
        public ManifestBspNode? Less { get; init; }
        public ManifestBspNode? Greater { get; init; }
        public int? LeafPoolElementOffset { get; init; }
        public ushort[]? FaceIndices { get; init; }
    }
}
