namespace NeversoftMultitool.Core.Formats.GsDump;

/// <summary>One kicked vertex as it arrived at the GS, prior to rasterisation.</summary>
internal readonly record struct GsDrawVertexInfo(
    int GifTagIndex,
    int Vsync,
    ulong Tex0,
    int PrimType,
    float X,
    float Y,
    float Z,
    float S,
    float T,
    float Q,
    bool NoKick);
