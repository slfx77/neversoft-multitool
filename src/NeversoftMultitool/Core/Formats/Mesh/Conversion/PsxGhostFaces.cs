using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds the forced-blend "ghost" face list for a mesh whose every face is
///     loader-invisible (the disc bit7-set opaque class the M3dInit STP toggle
///     rejects). The engine still draws such an entity when its item flag 0x800
///     is set: M3d_Render ORs 0x40|ABR into the face flags through the
///     GlobalFaceFlags scratchpad mask, so the otherwise invisible faces render
///     semi-transparent — l1a2's Watcher head apparition. The copies flip on
///     bit 6 (semi-transparent); bit 7 is already set on this face class, so
///     <see cref="PsxFace.BlendRate" /> reads the additive rate and the faces
///     flow through the standard <c>__st1</c>/<c>__st3</c> material and lift
///     emission paths unchanged.
/// </summary>
internal static class PsxGhostFaces
{
    internal static IReadOnlyList<PsxFace> CreateForcedBlendFaces(PsxMesh mesh)
    {
        var ghosts = new List<PsxFace>(mesh.InvisibleFaces.Count);
        foreach (var face in mesh.InvisibleFaces)
        {
            ghosts.Add(new PsxFace
            {
                Flags = (ushort)(face.Flags | 0x0040),
                IsQuad = face.IsQuad,
                IsTextured = face.IsTextured,
                IsGouraud = face.IsGouraud,
                IsSemiTransparent = true,
                Index0 = face.Index0,
                Index1 = face.Index1,
                Index2 = face.Index2,
                Index3 = face.Index3,
                NormalIndex = face.NormalIndex,
                R = face.R,
                G = face.G,
                B = face.B,
                Mode = face.Mode,
                TextureHash = face.TextureHash,
                U0 = face.U0,
                V0 = face.V0,
                U1 = face.U1,
                V1 = face.V1,
                U2 = face.U2,
                V2 = face.V2,
                U3 = face.U3,
                V3 = face.V3,
                TextureCoordinates =
                [
                    face.GetTextureCoordinate(0),
                    face.GetTextureCoordinate(1),
                    face.GetTextureCoordinate(2),
                    face.GetTextureCoordinate(3)
                ]
            });
        }

        return ghosts;
    }
}
