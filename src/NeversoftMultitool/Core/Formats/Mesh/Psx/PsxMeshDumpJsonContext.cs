using System.Text.Json.Serialization;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(PsxMeshDumpSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpAttachmentSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpObjectSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpMeshSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpVertexSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpFaceReadSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpFaceSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpTextureCoordinateSnapshot))]
[JsonSerializable(typeof(PsxMeshDumpVector3Snapshot))]
internal partial class PsxMeshDumpJsonContext : JsonSerializerContext;
