namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>Where one glyph sits in a packed <see cref="FntAtlas" />.</summary>
public sealed record FntAtlasCell(int GlyphIndex, int X, int Y, int Width, int Height);

/// <summary>A packed RGBA sheet holding every glyph of one font.</summary>
public sealed record FntAtlas(byte[] Rgba, int Width, int Height, int Padding, FntAtlasCell[] Cells);
