namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record Ps2WorldzoneRenderMetadata(
    string SourceName,
    int MdlCount,
    string TimeOfDay,
    float CoordinateScale)
    : NativeRenderMetadata("ps2_worldzone");
