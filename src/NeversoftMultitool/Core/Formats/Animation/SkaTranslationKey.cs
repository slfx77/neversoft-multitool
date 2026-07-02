using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>Decompressed translation keyframe (position + timestamp).</summary>
public readonly struct SkaTranslationKey(float time, Vector3 translation)
{
    public float Time { get; } = time;
    public Vector3 Translation { get; } = translation;
}
