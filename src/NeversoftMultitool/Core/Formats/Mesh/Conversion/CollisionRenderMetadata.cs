namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public sealed record CollisionRenderMetadata(int ObjectCount)
    : NativeRenderMetadata("collision");
