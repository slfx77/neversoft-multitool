using System.Text.Json.Serialization;

namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     Source-generated serialization for the oracle artifacts (mirrors the
///     <see cref="GsDumpAuditJsonContext" /> AOT pattern).
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GsOracleReport))]
[JsonSerializable(typeof(GsTextureOracleReport))]
internal sealed partial class GsOracleJsonContext : JsonSerializerContext;
