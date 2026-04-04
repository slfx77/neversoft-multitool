namespace NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;

internal readonly record struct VramUploadDescriptor(
    uint Dbp,
    uint Dbw,
    uint Dpsm,
    int Width,
    int Height,
    int ImageDataOffset,
    int ImageDataSize,
    int DataSize,
    uint RelativeDataOffset)
{
    public int ImageDataEndOffset => ImageDataOffset + ImageDataSize;
}
