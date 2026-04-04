namespace NeversoftMultitool.Core.Formats.Texture.Psx;

/// <summary>
///     Options controlling ancillary output formats (DDS, mip atlas) during texture extraction.
/// </summary>
internal readonly record struct OutputOptions(bool WriteDds, bool WriteMipAtlas);
