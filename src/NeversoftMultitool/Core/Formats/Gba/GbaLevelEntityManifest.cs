using System.Text.Json.Serialization;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>One entity record, as inspection JSON.</summary>
/// <remarks>
///     Named by index past the first two on purpose — see
///     <see cref="GbaLevelEntity" /> for what is and is not established.
/// </remarks>
public sealed record GbaLevelEntitySnapshot(
    int WorldX,
    int WorldY,
    int CellX,
    int CellY,
    int Field2,
    int Field3,
    int Field4,
    int Field5,
    int Field6,
    int Field7);

/// <summary>One level record's table.</summary>
public sealed record GbaLevelEntityTableSnapshot(
    int RecordIndex,
    int RecordOffset,
    string? Name,
    int TableOffset,
    int EntityCount,
    IReadOnlyList<GbaLevelEntitySnapshot> Entities);

/// <summary>Schema-v1 inspection output for a ROM's entity tables.</summary>
public sealed record GbaLevelEntityManifest(
    int SchemaVersion,
    string Source,
    int SourceBytes,
    int TableField,
    int RecordBytes,
    int RawUnitsPerCell,
    int LevelRecordCount,
    int EntityCount,
    string FieldInterpretationStatus,
    string GeometryApplicationStatus,
    IReadOnlyList<GbaLevelEntityTableSnapshot> Levels);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GbaLevelEntityManifest))]
internal sealed partial class GbaLevelEntityJsonContext : JsonSerializerContext;
