using System.Numerics;
using System.Text.Json;

namespace NeversoftMultitool.Core.Formats.Psx;

internal sealed class PsxMeshDumpSnapshot
{
    public required string FileName { get; init; }
    public required ushort Version { get; init; }
    public required bool HasHierarchy { get; init; }
    public required float ScaleDivisor { get; init; }
    public required float TranslationDivisor { get; init; }
    public required IReadOnlyList<PsxMeshDumpAttachmentSnapshot> Attachments { get; init; }
    public required IReadOnlyList<PsxMeshDumpObjectSnapshot> Objects { get; init; }
    public required IReadOnlyList<PsxMeshDumpMeshSnapshot> Meshes { get; init; }
}

internal sealed class PsxMeshDumpAttachmentSnapshot
{
    public required uint AttachmentIndex { get; init; }
    public required int MeshIndex { get; init; }
    public required int ObjectIndex { get; init; }
    public required int VertexIndex { get; init; }
    public required PsxMeshDumpVector3Snapshot LocalPosition { get; init; }
    public required PsxMeshDumpVector3Snapshot WorldPosition { get; init; }
}

internal sealed class PsxMeshDumpObjectSnapshot
{
    public required int ObjectIndex { get; init; }
    public required uint Flags { get; init; }
    public required ushort MeshIndex { get; init; }
    public required int ParentIndex { get; init; }
    public required int RawX { get; init; }
    public required int RawY { get; init; }
    public required int RawZ { get; init; }
    public required PsxMeshDumpVector3Snapshot Position { get; init; }
}

internal sealed class PsxMeshDumpMeshSnapshot
{
    public required int MeshIndex { get; init; }
    public required int ObjectIndex { get; init; }
    public required int VertexCount { get; init; }
    public required int NormalCount { get; init; }
    public required int FaceCount { get; init; }
    public required int RawFaceCount { get; init; }
    public required int StitchSourceCount { get; init; }
    public required int StitchedReferenceCount { get; init; }
    public required int StitchFailureCount { get; init; }
    public required short LodDepth { get; init; }
    public required ushort LodNextMeshIndex { get; init; }
    public required bool HasPerVertexNormals { get; init; }
    public required IReadOnlyList<PsxMeshDumpVertexSnapshot> Vertices { get; init; }
    public required IReadOnlyList<PsxMeshDumpFaceReadSnapshot> FaceReads { get; init; }
    public required IReadOnlyList<PsxMeshDumpFaceSnapshot> Faces { get; init; }
}

internal sealed class PsxMeshDumpVertexSnapshot
{
    public required int VertexIndex { get; init; }
    public required ushort Type { get; init; }
    public required short RawX { get; init; }
    public required short RawY { get; init; }
    public required short RawZ { get; init; }
    public required uint? AttachmentIndex { get; init; }
    public required uint? AttachmentTargetIndex { get; init; }
    public required PsxMeshDumpVector3Snapshot LocalPosition { get; init; }
    public required PsxMeshDumpVector3Snapshot WorldPosition { get; init; }
    public required bool UsedAttachment { get; init; }
    public required bool AttachmentResolved { get; init; }
    public required int SourceMeshIndex { get; init; }
    public required int SourceObjectIndex { get; init; }
    public required int SourceVertexIndex { get; init; }
}

internal sealed class PsxMeshDumpFaceReadSnapshot
{
    public required int RawFaceIndex { get; init; }
    public required long Offset { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Length { get; init; }
    public required int BytesConsumed { get; init; }
    public required int UnderreadBytes { get; init; }
    public required int OverreadBytes { get; init; }
    public required bool IsLengthAligned { get; init; }
    public required bool IsAccepted { get; init; }
    public required int? AcceptedFaceIndex { get; init; }
    public required string? RejectionReason { get; init; }
}

internal sealed class PsxMeshDumpFaceSnapshot
{
    public required int FaceIndex { get; init; }
    public required int RawFaceIndex { get; init; }
    public required ushort Flags { get; init; }
    public required ushort Length { get; init; }
    public required bool IsQuad { get; init; }
    public required bool IsTextured { get; init; }
    public required bool IsGouraud { get; init; }
    public required bool IsSemiTransparent { get; init; }
    public required uint NormalIndex { get; init; }
    public required uint TextureHash { get; init; }
    public required IReadOnlyList<uint> Indices { get; init; }
    public required IReadOnlyList<PsxMeshDumpTextureCoordinateSnapshot> TextureCoordinates { get; init; }
    public required IReadOnlyList<PsxMeshDumpVector3Snapshot> ResolvedWorldVertices { get; init; }
}

internal sealed class PsxMeshDumpTextureCoordinateSnapshot
{
    public required int U { get; init; }
    public required int V { get; init; }
}

internal sealed class PsxMeshDumpVector3Snapshot
{
    public required float X { get; init; }
    public required float Y { get; init; }
    public required float Z { get; init; }
}

internal static class PsxMeshDumpSnapshotBuilder
{
    internal static PsxMeshDumpSnapshot Build(string filePath)
    {
        var psxFile = PsxMeshFile.Parse(filePath)
                      ?? throw new InvalidDataException($"No mesh data found in {filePath}");
        return Build(psxFile, Path.GetFileName(filePath));
    }

    internal static PsxMeshDumpSnapshot Build(PsxMeshFile psxFile, string fileName = "")
    {
        var attachments = psxFile.AttachmentVertices
            .Select(a =>
            {
                var objectIndex = PsxCharacterMeshResolver.GetObjectIndex(psxFile, a.MeshIndex);
                var worldPosition = a.LocalPosition + PsxCharacterMeshResolver.GetObjectOffset(psxFile, a.MeshIndex);
                return new PsxMeshDumpAttachmentSnapshot
                {
                    AttachmentIndex = a.AttachmentIndex,
                    MeshIndex = a.MeshIndex,
                    ObjectIndex = objectIndex,
                    VertexIndex = a.VertexIndex,
                    LocalPosition = CreateVector3Snapshot(a.LocalPosition),
                    WorldPosition = CreateVector3Snapshot(worldPosition)
                };
            })
            .ToList();

        var objects = psxFile.Objects
            .Select((obj, objectIndex) => new PsxMeshDumpObjectSnapshot
            {
                ObjectIndex = objectIndex,
                Flags = obj.Flags,
                MeshIndex = obj.MeshIndex,
                ParentIndex = obj.ParentIndex,
                RawX = obj.RawX,
                RawY = obj.RawY,
                RawZ = obj.RawZ,
                Position = CreateVector3Snapshot(PsxMeshSemantics.GetObjectOffset(obj, psxFile.TranslationDivisor))
            })
            .ToList();

        var meshes = new List<PsxMeshDumpMeshSnapshot>(psxFile.Meshes.Count);
        for (var meshIndex = 0; meshIndex < psxFile.Meshes.Count; meshIndex++)
        {
            var mesh = psxFile.Meshes[meshIndex];
            var objectIndex = PsxCharacterMeshResolver.GetObjectIndex(psxFile, meshIndex);

            var vertices = mesh.Vertices
                .Select((vertex, vertexIndex) =>
                {
                    var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, (uint)vertexIndex);
                    return new PsxMeshDumpVertexSnapshot
                    {
                        VertexIndex = vertexIndex,
                        Type = vertex.Type,
                        RawX = vertex.RawX,
                        RawY = vertex.RawY,
                        RawZ = vertex.RawZ,
                        AttachmentIndex = vertex.AttachmentIndex,
                        AttachmentTargetIndex = vertex.AttachmentTargetIndex,
                        LocalPosition = CreateVector3Snapshot(new Vector3(vertex.X, vertex.Y, vertex.Z)),
                        WorldPosition = CreateVector3Snapshot(resolved.WorldPosition),
                        UsedAttachment = resolved.UsedAttachment,
                        AttachmentResolved = resolved.AttachmentResolved,
                        SourceMeshIndex = resolved.SourceMeshIndex,
                        SourceObjectIndex = resolved.SourceObjectIndex,
                        SourceVertexIndex = resolved.SourceVertexIndex
                    };
                })
                .ToList();

            var faceReads = mesh.FaceReadInfos
                .Select(info => new PsxMeshDumpFaceReadSnapshot
                {
                    RawFaceIndex = info.RawFaceIndex,
                    Offset = info.Offset,
                    Flags = info.Flags,
                    Length = info.Length,
                    BytesConsumed = info.BytesConsumed,
                    UnderreadBytes = info.UnderreadBytes,
                    OverreadBytes = info.OverreadBytes,
                    IsLengthAligned = info.IsLengthAligned,
                    IsAccepted = info.IsAccepted,
                    AcceptedFaceIndex = info.AcceptedFaceIndex,
                    RejectionReason = info.RejectionReason
                })
                .ToList();

            var faceReadByAcceptedIndex = mesh.FaceReadInfos
                .Where(info => info.AcceptedFaceIndex.HasValue)
                .ToDictionary(info => info.AcceptedFaceIndex!.Value);

            var faces = mesh.Faces
                .Select((face, faceIndex) =>
                {
                    faceReadByAcceptedIndex.TryGetValue(faceIndex, out var faceReadInfo);
                    var slots = face.IsQuad ? 4 : 3;
                    var resolvedVertices = new List<PsxMeshDumpVector3Snapshot>(slots);
                    var indices = new List<uint>(slots);
                    var textureCoordinates = new List<PsxMeshDumpTextureCoordinateSnapshot>(slots);

                    for (var slot = 0; slot < slots; slot++)
                    {
                        var vertexIndex = GetFaceVertexIndex(face, slot);
                        var resolved = PsxCharacterMeshResolver.ResolveVertex(psxFile, meshIndex, vertexIndex);
                        var texCoord = face.GetTextureCoordinate(slot);

                        indices.Add(vertexIndex);
                        resolvedVertices.Add(CreateVector3Snapshot(resolved.WorldPosition));
                        textureCoordinates.Add(new PsxMeshDumpTextureCoordinateSnapshot
                        {
                            U = texCoord.U,
                            V = texCoord.V
                        });
                    }

                    return new PsxMeshDumpFaceSnapshot
                    {
                        FaceIndex = faceIndex,
                        RawFaceIndex = faceReadInfo?.RawFaceIndex ?? faceIndex,
                        Flags = face.Flags,
                        Length = faceReadInfo?.Length ?? 0,
                        IsQuad = face.IsQuad,
                        IsTextured = face.IsTextured,
                        IsGouraud = face.IsGouraud,
                        IsSemiTransparent = face.IsSemiTransparent,
                        NormalIndex = face.NormalIndex,
                        TextureHash = face.TextureHash,
                        Indices = indices,
                        TextureCoordinates = textureCoordinates,
                        ResolvedWorldVertices = resolvedVertices
                    };
                })
                .ToList();

            meshes.Add(new PsxMeshDumpMeshSnapshot
            {
                MeshIndex = meshIndex,
                ObjectIndex = objectIndex,
                VertexCount = mesh.Vertices.Count,
                NormalCount = mesh.Normals.Count,
                FaceCount = mesh.Faces.Count,
                RawFaceCount = mesh.FaceReadInfos.Count,
                StitchSourceCount = mesh.Vertices.Count(v => PsxMeshSemantics.IsExactStitchSource(v.Type)),
                StitchedReferenceCount = mesh.Vertices.Count(v => PsxMeshSemantics.IsExactStitchedReference(v.Type)),
                StitchFailureCount = mesh.StitchFailureCount,
                LodDepth = mesh.LodDepth,
                LodNextMeshIndex = mesh.LodNextMeshIndex,
                HasPerVertexNormals = mesh.HasPerVertexNormals,
                Vertices = vertices,
                FaceReads = faceReads,
                Faces = faces
            });
        }

        return new PsxMeshDumpSnapshot
        {
            FileName = fileName,
            Version = psxFile.Version,
            HasHierarchy = psxFile.HasHierarchy,
            ScaleDivisor = psxFile.ScaleDivisor,
            TranslationDivisor = psxFile.TranslationDivisor,
            Attachments = attachments,
            Objects = objects,
            Meshes = meshes
        };
    }

    internal static string Serialize(PsxMeshDumpSnapshot snapshot)
    {
        return JsonSerializer.Serialize(snapshot, PsxMeshDumpJsonContext.Default.PsxMeshDumpSnapshot);
    }

    private static PsxMeshDumpVector3Snapshot CreateVector3Snapshot(Vector3 value)
    {
        return new PsxMeshDumpVector3Snapshot
        {
            X = value.X,
            Y = value.Y,
            Z = value.Z
        };
    }

    private static uint GetFaceVertexIndex(PsxFace face, int slot)
    {
        return slot switch
        {
            0 => face.Index0,
            1 => face.Index1,
            2 => face.Index2,
            3 => face.Index3,
            _ => throw new ArgumentOutOfRangeException(nameof(slot))
        };
    }
}
