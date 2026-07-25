using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal sealed record PsxSurfaceAnimationResult(
    Vector4[]? Palette,
    IReadOnlyList<PsxColourPulse> ColourPulses);
