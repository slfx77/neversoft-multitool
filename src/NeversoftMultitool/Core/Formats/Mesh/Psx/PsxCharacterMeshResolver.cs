using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal static class PsxCharacterMeshResolver
{
    internal static int GetObjectIndex(PsxMeshFile psxFile, int meshIndex)
    {
        return PsxMeshSemantics.GetCharacterObjectIndex(psxFile, meshIndex);
    }

    internal static Vector3 GetObjectOffset(PsxMeshFile psxFile, int meshIndex)
    {
        return PsxMeshSemantics.GetCharacterObjectOffset(psxFile, meshIndex);
    }

    internal static PsxResolvedVertex ResolveVertex(PsxMeshFile psxFile, int meshIndex, uint vertexIndex)
    {
        var mesh = psxFile.Meshes[meshIndex];
        var vertex = mesh.Vertices[(int)vertexIndex];
        var localPosition = new Vector3(vertex.X, vertex.Y, vertex.Z);
        var fallbackWorldPosition = localPosition + GetObjectOffset(psxFile, meshIndex);
        var sourceObjectIndex = GetObjectIndex(psxFile, meshIndex);

        if (!PsxMeshSemantics.IsExactStitchedReference(vertex.Type))
        {
            return new PsxResolvedVertex(
                fallbackWorldPosition,
                vertex.AttachmentIndex,
                false,
                true,
                meshIndex,
                sourceObjectIndex,
                (int)vertexIndex);
        }

        var attachmentIndex = vertex.AttachmentTargetIndex;
        if (attachmentIndex.HasValue
            && psxFile.AttachmentVertexMap.TryGetValue(attachmentIndex.Value, out var attachment))
        {
            var attachmentWorldPosition = attachment.LocalPosition + GetObjectOffset(psxFile, attachment.MeshIndex);
            var attachmentObjectIndex = GetObjectIndex(psxFile, attachment.MeshIndex);
            return new PsxResolvedVertex(
                attachmentWorldPosition,
                attachment.AttachmentIndex,
                true,
                true,
                attachment.MeshIndex,
                attachmentObjectIndex,
                attachment.VertexIndex);
        }

        return new PsxResolvedVertex(
            fallbackWorldPosition,
            attachmentIndex,
            true,
            false,
            -1,
            -1,
            -1);
    }

    internal sealed record PsxResolvedVertex(
        Vector3 WorldPosition,
        uint? AttachmentIndex,
        bool UsedAttachment,
        bool AttachmentResolved,
        int SourceMeshIndex,
        int SourceObjectIndex,
        int SourceVertexIndex);
}
