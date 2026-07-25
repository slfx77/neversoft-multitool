namespace NeversoftMultitool.Core.Rendering;

/// <summary>A named camera angle for still renders.</summary>
public readonly record struct RenderView(string Name, float Azimuth, float Elevation);
