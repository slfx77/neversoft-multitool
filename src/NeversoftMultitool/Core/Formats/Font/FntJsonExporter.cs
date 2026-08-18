using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>Writes deterministic schema-v1 inspection JSON for PS1-era Neversoft bitmap fonts.</summary>
public static class FntJsonExporter
{
    public const string SchemaName = "neversoft.bitmapFont";
    public const int CurrentSchemaVersion = 1;

    private const string CharacterMapNoteText =
        "Font::CharMap is runtime state assigned by game code; no .fnt records which ordering " +
        "its author used, and the orderings are not stable across builds (the THPS2 prototype's " +
        "s2bio.fnt maps glyph 48 to 'a', retail THPS2's puts '_' there and shifts lowercase to " +
        "49). A candidate mapping every glyph is therefore not evidence it is the right one.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    public static string Serialize(
        string sourceFile,
        FntFile font,
        FntAtlas atlas,
        string atlasFileName,
        FntCharacterMap.Mode? characterMapMode = null)
    {
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(atlas);

        var characters = characterMapMode is { } mode
            ? FntCharacterMap.Characters(mode, font.Glyphs.Length)
            : null;

        var manifest = new Manifest
        {
            Schema = SchemaName,
            SchemaVersion = CurrentSchemaVersion,
            Source = Path.GetFileName(sourceFile),
            ByteOrder = "littleEndian",
            SerializedSize = font.SerializedSize,
            SerializedSha256 = font.SerializedSha256,
            Layout = font.Layout switch
            {
                FntLayout.PalettedWithAdvance => "palettedWithAdvance",
                FntLayout.CompactWithoutPalette => "compactWithoutPalette",
                _ => throw new InvalidDataException($"Unsupported font layout {font.Layout}")
            },
            PixelFormat = font.HasPalette ? "clut4" : "unpaletted4",
            PixelNibbleOrder = font.HasPalette ? "lowNibbleFirst" : "highNibbleFirst",
            // How the exporter chose to render the values, not a claim about what they mean:
            // with no CLUT in the file and no decompiled Dreamcast loader, coverage and
            // palette-index readings are indistinguishable from the bytes.
            PixelInterpretation = font.HasPalette ? "paletteIndex" : "coverageAlphaOnWhite",
            GlyphCount = font.Glyphs.Length,
            CharacterMapStatus = characterMapMode is null ? "notApplied" : "appliedFromCallerArgument",
            CharacterMapMode = characterMapMode is { } applied ? (int)applied : null,
            CharacterMapNote = CharacterMapNoteText,
            CharacterMapCandidates = Enum.GetValues<FntCharacterMap.Mode>()
                .Select(candidate => new ManifestCharacterMapCandidate
                {
                    Mode = (int)candidate,
                    Name = CandidateName(candidate),
                    MapsEveryGlyph = FntCharacterMap.MapsEveryGlyph(candidate, font.Glyphs.Length),
                    GlyphCountMatchesFullSet =
                        font.Glyphs.Length == FntCharacterMap.HighestMappedIndex(candidate) + 1,
                    UnmappedGlyphCount = Math.Max(
                        0,
                        font.Glyphs.Length - (FntCharacterMap.HighestMappedIndex(candidate) + 1))
                })
                .ToArray(),
            Atlas = new ManifestAtlas
            {
                File = atlasFileName,
                Width = atlas.Width,
                Height = atlas.Height,
                Padding = atlas.Padding
            },
            Palette = font.HasPalette
                ? Enumerable.Range(0, font.Palette.Length)
                    .Select(index => ToManifestPaletteEntry(font, index))
                    .ToArray()
                : null,
            FallbackGlyph = ToManifestFallbackGlyph(font),
            Glyphs = font.Glyphs
                .Select(glyph => ToManifestGlyph(glyph, atlas.Cells[glyph.Index], characters))
                .ToArray()
        };

        return JsonSerializer.Serialize(manifest, JsonOptions);
    }

    private static string CandidateName(FntCharacterMap.Mode mode)
    {
        return mode switch
        {
            FntCharacterMap.Mode.Standard => "standard",
            FntCharacterMap.Mode.Numeric => "numeric",
            FntCharacterMap.Mode.LowercaseFirst => "lowercaseFirst",
            _ => throw new InvalidDataException($"Unsupported character map mode {mode}")
        };
    }

    /// <summary>
    ///     Reports each CLUT entry through the same conversion the atlas uses, so the manifest
    ///     and the PNG can never disagree about a colour.
    /// </summary>
    private static ManifestPaletteEntry ToManifestPaletteEntry(FntFile font, int index)
    {
        var entry = font.Palette[index];
        Span<byte> rgba = stackalloc byte[4];
        FntAtlasBuilder.ToRgba(font, (byte)index, rgba);

        return new ManifestPaletteEntry
        {
            Index = index,
            Raw = $"0x{entry:X4}",
            Red = rgba[0],
            Green = rgba[1],
            Blue = rgba[2],
            Alpha = rgba[3],
            SemiTransparentFlag = (entry & 0x8000) != 0
        };
    }

    /// <summary>
    ///     The unmapped-character box the loader synthesizes at index <c>glyphCount</c>: it
    ///     borrows glyph 0's metrics and fills a same-sized bitmap with the byte <c>0x12</c>
    ///     (<c>FONTTOOLS.cpp:295-329</c>). It is derived, never stored, so it is reported here
    ///     and deliberately kept out of the atlas, which holds authored art only.
    /// </summary>
    private static ManifestFallbackGlyph? ToManifestFallbackGlyph(FntFile font)
    {
        // The prototype loader only reads the paletted layout, so it says nothing about how a
        // palette-less font would build its fallback. Claim nothing there.
        if (!font.HasPalette)
            return null;

        var first = font.Glyphs[0];
        return new ManifestFallbackGlyph
        {
            Index = font.Glyphs.Length,
            Width = first.Width,
            Height = first.Height,
            Baseline = first.Baseline,
            AdvanceWidth = first.AdvanceWidth,
            FillByte = "0x12",
            Derivation = "synthesizedByLoaderFromGlyph0; not stored in the file"
        };
    }

    private static ManifestGlyph ToManifestGlyph(FntGlyph glyph, FntAtlasCell cell, string?[]? characters)
    {
        return new ManifestGlyph
        {
            Index = glyph.Index,
            Character = characters is null ? null : characters[glyph.Index],
            Width = glyph.Width,
            Height = glyph.Height,
            Baseline = glyph.Baseline,
            AdvanceWidth = glyph.AdvanceWidth,
            WidthUnits = glyph.WidthUnits,
            PixelDataOffset = glyph.PixelDataOffset,
            PixelDataLength = glyph.PixelDataLength,
            AtlasX = cell.X,
            AtlasY = cell.Y
        };
    }

    private sealed class Manifest
    {
        public required string Schema { get; init; }
        public required int SchemaVersion { get; init; }
        public required string Source { get; init; }
        public required string ByteOrder { get; init; }
        public required int SerializedSize { get; init; }
        public required string SerializedSha256 { get; init; }
        public required string Layout { get; init; }
        public required string PixelFormat { get; init; }
        public required string PixelNibbleOrder { get; init; }
        public required string PixelInterpretation { get; init; }
        public required int GlyphCount { get; init; }
        public required string CharacterMapStatus { get; init; }
        public required int? CharacterMapMode { get; init; }
        public required string CharacterMapNote { get; init; }
        public required ManifestCharacterMapCandidate[] CharacterMapCandidates { get; init; }
        public required ManifestAtlas Atlas { get; init; }
        public required ManifestPaletteEntry[]? Palette { get; init; }
        public required ManifestFallbackGlyph? FallbackGlyph { get; init; }
        public required ManifestGlyph[] Glyphs { get; init; }
    }

    private sealed class ManifestCharacterMapCandidate
    {
        public required int Mode { get; init; }
        public required string Name { get; init; }
        public required bool MapsEveryGlyph { get; init; }
        public required bool GlyphCountMatchesFullSet { get; init; }
        public required int UnmappedGlyphCount { get; init; }
    }

    private sealed class ManifestAtlas
    {
        public required string File { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Padding { get; init; }
    }

    private sealed class ManifestPaletteEntry
    {
        public required int Index { get; init; }
        public required string Raw { get; init; }
        public required int Red { get; init; }
        public required int Green { get; init; }
        public required int Blue { get; init; }
        public required int Alpha { get; init; }
        public required bool SemiTransparentFlag { get; init; }
    }

    private sealed class ManifestFallbackGlyph
    {
        public required int Index { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Baseline { get; init; }
        public required int? AdvanceWidth { get; init; }
        public required string FillByte { get; init; }
        public required string Derivation { get; init; }
    }

    private sealed class ManifestGlyph
    {
        public required int Index { get; init; }
        public required string? Character { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required int Baseline { get; init; }
        public required int? AdvanceWidth { get; init; }
        public required int WidthUnits { get; init; }
        public required int PixelDataOffset { get; init; }
        public required int PixelDataLength { get; init; }
        public required int AtlasX { get; init; }
        public required int AtlasY { get; init; }
    }
}
