using System.Text.Json.Serialization;

namespace NeversoftMultitool.Core.Formats.GsDump;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(GsDumpAuditReport))]
internal sealed partial class GsDumpAuditJsonContext : JsonSerializerContext;
