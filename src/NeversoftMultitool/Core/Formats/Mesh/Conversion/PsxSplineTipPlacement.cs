using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal readonly record struct PsxSplineTipPlacement(
    int JointIndex,
    Vector3 Center,
    Vector3 Tangent,
    Vector3 Normal,
    Vector3 Binormal,
    int ForwardSign)
{
    internal Vector3 TransformPosition(Vector3 local)
    {
        return Center + TransformDirection(local);
    }

    internal Vector3 TransformDirection(Vector3 local)
    {
        // Reversing only local Z would reflect the tip and invert its winding.
        // Reversing local X as well is a 180-degree rotation about local Y, so
        // positions, normals, and front-face handedness remain consistent.
        return Normal * (ForwardSign * local.X)
               + Binormal * local.Y
               + Tangent * (ForwardSign * local.Z);
    }
}
