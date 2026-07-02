using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Decompressed rotation keyframe (unit quaternion + timestamp).</summary>
public readonly struct SkaRotationKey(float time, Quaternion rotation)
{
    public float Time { get; } = time;
    public Quaternion Rotation { get; } = rotation;
}
