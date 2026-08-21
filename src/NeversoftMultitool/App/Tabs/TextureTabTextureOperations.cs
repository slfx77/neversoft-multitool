using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Font;
using NeversoftMultitool.Core.Formats.Texture.Gba;
using NeversoftMultitool.Core.Formats.Texture.N64;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.Pvr;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool;

internal static class TextureTabTextureOperations
{
    private static readonly string[] CompoundTextureExtensions =
    [
        ".tex.xbx", ".img.xbx", ".tex.wpc", ".img.wpc",
        ".tex.ps2", ".img.ps2", ".tex.ngc", ".img.ngc", ".stex",
        ".tex.n64"
    ];

    private static readonly string[] NgcTexExtensions = [".tex.ngc", ".img.ngc"];
    private static readonly string[] N64TexExtensions = [".tex.n64"];

    // .stex = THAW level/zone textures: DXT containers on PC, zone TEX on PS2 —
    // ParseXbxTextures dispatches by content, mirroring the CLI xbxtex/ps2tex routing.
    private static readonly string[] XboxTexExtensions = [".tex.xbx", ".tex.wpc", ".stex"];
    private static readonly string[] XboxImgExtensions = [".img.xbx", ".img.wpc"];
    private static readonly string[] Ps2TexExtensions = [".tex.ps2", ".img.ps2", ".tex", ".img"];

    public static bool IsTextureFile(string path)
    {
        var name = Path.GetFileName(path);
        if (OrdinalFileName.HasAnySuffix(name, CompoundTextureExtensions))
            return true;

        var ext = Path.GetExtension(path);
        return ext.Equals(".psx", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".pvr", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tex", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".img", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".fnt", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".gba", StringComparison.OrdinalIgnoreCase);
    }

    public static TextureFileFormat ClassifyFormat(string fileName)
    {
        if (OrdinalFileName.HasAnySuffix(fileName, N64TexExtensions))
            return TextureFileFormat.N64Tex;
        // Must precede the PSX fallthrough below, which would otherwise claim .fnt.
        if (OrdinalFileName.HasSuffix(fileName, ".fnt"))
            return TextureFileFormat.Fnt;
        if (OrdinalFileName.HasSuffix(fileName, ".gba"))
            return TextureFileFormat.GbaImage;
        if (OrdinalFileName.HasAnySuffix(fileName, NgcTexExtensions))
            return TextureFileFormat.NgcTex;
        if (OrdinalFileName.HasAnySuffix(fileName, XboxTexExtensions))
            return TextureFileFormat.XbxTex;
        if (OrdinalFileName.HasAnySuffix(fileName, XboxImgExtensions))
            return TextureFileFormat.XbxImg;
        if (OrdinalFileName.HasAnySuffix(fileName, Ps2TexExtensions))
            return TextureFileFormat.Ps2Tex;

        if (OrdinalFileName.HasSuffix(fileName, ".pvr"))
            return TextureFileFormat.Pvr;

        return TextureFileFormat.Psx;
    }

    public static int CountTextures(AssetSource source, TextureFileFormat format)
    {
        var data = source.ReadBytes();
        return format switch
        {
            TextureFileFormat.Ps2Tex => CountParsedTextures(Ps2TextureParser.Parse(data)),
            TextureFileFormat.NgcTex => CountParsedTextures(NgcTexFile.Parse(data)),
            TextureFileFormat.XbxTex => CountParsedTextures(ParseXbxTextures(data, format)),
            TextureFileFormat.XbxImg => CountParsedTextures(ParseXbxTextures(data, format)),
            TextureFileFormat.Pvr => PvrFileDecoder.DecodeToRgba(data) != null ? 1 : 0,
            TextureFileFormat.N64Tex => N64TexFile.IsN64Texture(data) ? 1 : 0,
            TextureFileFormat.Fnt => TryParseFnt(data)?.Glyphs.Length ?? 0,
            TextureFileFormat.GbaImage => GbaRomImages.ScanFullScreenImages(data).Count,
            _ => PsxLibrary.EnumerateTextures(data).Count
        };
    }

    public static List<PsxTextureEntry> EnumerateChildren(PsxFileEntry parent)
    {
        var source = parent.Source;
        var data = source.ReadBytes();
        return parent.Format switch
        {
            TextureFileFormat.Ps2Tex => BuildPs2Entries(Ps2TextureParser.Parse(data), parent),
            TextureFileFormat.NgcTex => BuildNgcEntries(NgcTexFile.Parse(data), parent),
            TextureFileFormat.XbxTex or TextureFileFormat.XbxImg => BuildXboxEntries(data, parent),
            TextureFileFormat.Pvr => BuildPvrEntries(data, parent),
            TextureFileFormat.N64Tex => BuildN64Entries(data, parent),
            TextureFileFormat.Fnt => BuildFntEntries(data, parent),
            TextureFileFormat.GbaImage => BuildGbaEntries(data, parent),
            _ => BuildPsxEntries(data, parent)
        };
    }

    public static (int totalTex, int writtenTex, bool skipped, bool success) ExtractTextures(
        PsxFileEntry entry,
        string outputDir,
        bool createSubDirs,
        bool writeDds,
        bool writeMipAtlas)
    {
        var stem = StripCompoundExtension(entry.FileName);
        var data = entry.Source.ReadBytes();

        return entry.Format switch
        {
            TextureFileFormat.Ps2Tex => ExtractPs2Textures(data, outputDir, stem),
            TextureFileFormat.NgcTex => ExtractNgcTextures(data, outputDir, stem),
            TextureFileFormat.XbxTex => ExtractXbxTextures(data, outputDir, stem, entry.Format),
            TextureFileFormat.XbxImg => ExtractXbxImage(data, outputDir, stem, createSubDirs),
            TextureFileFormat.Pvr => ExtractPvr(data, outputDir, stem, createSubDirs),
            TextureFileFormat.N64Tex => ExtractN64Texture(data, outputDir, stem, createSubDirs),
            TextureFileFormat.GbaImage => ExtractGbaImages(data, outputDir, stem),
            // A font is one packed atlas plus one metrics manifest rather than a PNG per glyph,
            // and .fnt is a simple extension the compound-suffix stripper leaves intact.
            TextureFileFormat.Fnt => ExtractFnt(
                data,
                outputDir,
                Path.GetFileNameWithoutExtension(entry.FileName),
                entry.FileName,
                createSubDirs),
            _ => ExtractPsxTextures(data, entry.FileName, outputDir, createSubDirs, writeDds, writeMipAtlas)
        };
    }

    public static (byte[] rgba, int width, int height)? GetPreviewRgba(
        AssetSource source,
        int textureIndex,
        TextureFileFormat format)
    {
        var data = source.ReadBytes();
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
                return GetPreviewTexture(Ps2TextureParser.Parse(data), textureIndex);
            case TextureFileFormat.NgcTex:
                return GetPreviewTexture(NgcTexFile.Parse(data), textureIndex);
            case TextureFileFormat.XbxTex:
                return GetPreviewTexture(ParseXbxTextures(data, format), textureIndex);
            case TextureFileFormat.XbxImg:
                return GetPreviewTexture(ParseXbxTextures(data, format), textureIndex);
            case TextureFileFormat.Pvr:
                return textureIndex == 0 ? PvrFileDecoder.DecodeToRgba(data) : null;
            case TextureFileFormat.N64Tex:
            {
                if (textureIndex != 0)
                    return null;
                var texture = TryDecodeN64(data);
                // A record remains one selectable texture. Preview its
                // authored level zero; extraction writes any stored mips.
                return texture != null ? (texture.Rgba, texture.Width, texture.Height) : null;
            }
            case TextureFileFormat.Fnt:
            {
                var font = TryParseFnt(data);
                if (font == null || textureIndex < 0 || textureIndex >= font.Glyphs.Length)
                    return null;

                var glyph = font.Glyphs[textureIndex];
                return (FntAtlasBuilder.RenderGlyph(font, glyph), glyph.Width, glyph.Height);
            }
            case TextureFileFormat.GbaImage:
            {
                var images = GbaRomImages.ScanFullScreenImages(data);
                if (textureIndex < 0 || textureIndex >= images.Count)
                    return null;
                return (images[textureIndex].Rgba, GbaRomImages.ScreenWidth, GbaRomImages.ScreenHeight);
            }
            default:
                return PsxLibrary.ExtractTextureAt(data, textureIndex, source.EntryName);
        }
    }

    private static int CountParsedTextures(Ps2TexResult result)
    {
        return result.Success ? result.Textures.Count(texture => texture.Pixels != null) : 0;
    }

    private static List<PsxTextureEntry> BuildPs2Entries(Ps2TexResult result, PsxFileEntry parent)
    {
        return result.Success
            ? result.Textures.Select((texture, index) => (Texture: texture, Index: index))
                .Where(item => item.Texture.Pixels != null)
                .Select(item => new PsxTextureEntry
                {
                    Parent = parent,
                    NameHash = item.Texture.Checksum,
                    Width = item.Texture.Width,
                    Height = item.Texture.Height,
                    PaletteType = Ps2TexFile.DescribePsm(item.Texture.Psm),
                    Index = item.Index,
                    ResolvedName = item.Texture.Name ?? QbKey.TryResolve(item.Texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildNgcEntries(Ps2TexResult result, PsxFileEntry parent)
    {
        return result.Success
            ? result.Textures.Select((texture, index) => (Texture: texture, Index: index))
                .Where(item => item.Texture.Pixels != null)
                .Select(item => new PsxTextureEntry
                {
                    Parent = parent,
                    NameHash = item.Texture.Checksum,
                    Width = item.Texture.Width,
                    Height = item.Texture.Height,
                    PaletteType = "NGC TEX",
                    Index = item.Index,
                    ResolvedName = item.Texture.Name ?? QbKey.TryResolve(item.Texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildXboxEntries(byte[] data, PsxFileEntry parent)
    {
        // The label reflects the decoder that actually succeeded — .stex is
        // shared between THAW PC DXT containers and PS2 zone TEX, so the
        // extension family alone must not claim "Xbox".
        var result = ParseXbxTextures(data, parent.Format, out var formatLabel);
        return result.Success
            ? result.Textures.Select((texture, index) => (Texture: texture, Index: index))
                .Where(item => item.Texture.Pixels != null)
                .Select(item => new PsxTextureEntry
                {
                    Parent = parent,
                    NameHash = item.Texture.Checksum,
                    Width = item.Texture.Width,
                    Height = item.Texture.Height,
                    PaletteType = formatLabel,
                    Index = item.Index,
                    ResolvedName = item.Texture.Name ?? QbKey.TryResolve(item.Texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildPvrEntries(byte[] data, PsxFileEntry parent)
    {
        var pvr = PvrFileDecoder.DecodeToRgba(data);
        return pvr != null
            ?
            [
                new PsxTextureEntry
                {
                    Parent = parent,
                    NameHash = 0,
                    Width = pvr.Value.Width,
                    Height = pvr.Value.Height,
                    PaletteType = "PVR",
                    Index = 0,
                    ResolvedName = Path.GetFileNameWithoutExtension(parent.FileName)
                }
            ]
            : [];
    }

    /// <summary>
    ///     N64 records hold exactly one texture; the embedded name (dictionary
    ///     records) beats the file stem, and psxtxt_&lt;id&gt; names carry the PS1
    ///     cross-platform texture id.
    /// </summary>
    private static List<PsxTextureEntry> BuildN64Entries(byte[] data, PsxFileEntry parent)
    {
        var texture = TryDecodeN64(data);
        return texture != null
            ?
            [
                new PsxTextureEntry
                {
                    Parent = parent,
                    NameHash = 0,
                    Width = texture.Width,
                    Height = texture.Height,
                    PaletteType = $"N64 {texture.Format}",
                    Index = 0,
                    ResolvedName = texture.Name ?? StripCompoundExtension(parent.FileName)
                }
            ]
            : [];
    }

    private static List<PsxTextureEntry> BuildFntEntries(byte[] data, PsxFileEntry parent)
    {
        var font = TryParseFnt(data);
        if (font == null)
            return [];

        var paletteType = font.HasPalette ? "4bpp CLUT" : "4bpp unpaletted";
        return font.Glyphs
            .Select(glyph => new PsxTextureEntry
            {
                Parent = parent,
                NameHash = 0,
                Width = glyph.Width,
                Height = glyph.Height,
                PaletteType = paletteType,
                Index = glyph.Index,
                // Glyphs are addressed by index, never by character: Font::CharMap is runtime
                // state and its ordering differs between builds of the same game.
                ResolvedName = $"glyph {glyph.Index:D3}"
            })
            .ToList();
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractFnt(
        byte[] data,
        string outputDir,
        string stem,
        string sourceFile,
        bool createSubDirs)
    {
        var font = TryParseFnt(data);
        if (font == null)
            return (0, 0, false, false);

        // Honour the per-file subdirectory option the same way the single-texture formats do,
        // keeping the atlas and its manifest together.
        var directory = createSubDirs ? Path.Combine(outputDir, stem) : outputDir;
        FntOutput.Write(directory, stem, sourceFile, font);
        return (font.Glyphs.Length, font.Glyphs.Length, false, true);
    }

    private static FntFile? TryParseFnt(byte[] data)
    {
        return FntFile.TryParse(data, out var font) ? font : null;
    }

    private static List<PsxTextureEntry> BuildGbaEntries(byte[] data, PsxFileEntry parent)
    {
        return GbaRomImages.ScanFullScreenImages(data)
            .Select((image, index) => new PsxTextureEntry
            {
                Parent = parent,
                NameHash = 0,
                Width = GbaRomImages.ScreenWidth,
                Height = GbaRomImages.ScreenHeight,
                PaletteType = $"GBA 8bpp {image.Layout}",
                Index = index,
                ResolvedName = image.Name
            })
            .ToList();
    }

    private static N64TexFile.N64Texture? TryDecodeN64(byte[] data)
    {
        try
        {
            return N64TexFile.IsN64Texture(data) ? N64TexFile.Decode(data) : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static List<PsxTextureEntry> BuildPsxEntries(byte[] data, PsxFileEntry parent)
    {
        return PsxLibrary.EnumerateTextures(data)
            .Select((texture, index) => new PsxTextureEntry
            {
                Parent = parent,
                NameHash = texture.NameHash,
                Width = texture.Header.Width,
                Height = texture.Header.Height,
                PaletteType = PsxLibrary.DescribePaletteType(texture.Header),
                Index = index,
                ResolvedName = QbKey.TryResolve(texture.NameHash)
            })
            .ToList();
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractPs2Textures(
        byte[] data,
        string outputDir,
        string stem)
    {
        var result = Ps2TextureParser.Parse(data);
        if (!result.Success)
            return (0, 0, false, false);

        var written = Ps2TexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractNgcTextures(
        byte[] data,
        string outputDir,
        string stem)
    {
        var result = NgcTexFile.Parse(data);
        if (!result.Success)
            return (0, 0, false, false);

        var written = NgcTexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractXbxTextures(
        byte[] data,
        string outputDir,
        string stem,
        TextureFileFormat format)
    {
        var result = ParseXbxTextures(data, format);
        if (!result.Success)
            return (0, 0, false, false);

        var written = XbxTexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractXbxImage(
        byte[] data,
        string outputDir,
        string stem,
        bool createSubDirs)
    {
        var result = ParseXbxTextures(data, TextureFileFormat.XbxImg);
        if (!result.Success)
            return (0, 0, false, false);

        var outPath = BuildSingleTextureOutputPath(outputDir, stem, createSubDirs);
        var written = XbxImgFile.SaveAsPng(result, outPath);
        return (1, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractPvr(
        byte[] data,
        string outputDir,
        string stem,
        bool createSubDirs)
    {
        var outPath = BuildSingleTextureOutputPath(outputDir, stem, createSubDirs);
        var ok = PvrFileDecoder.DecodeToPng(data, outPath);
        return (1, ok ? 1 : 0, false, ok);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractN64Texture(
        byte[] data,
        string outputDir,
        string stem,
        bool createSubDirs)
    {
        var texture = TryDecodeN64(data);
        if (texture == null)
            return (0, 0, false, false);

        var outPath = BuildSingleTextureOutputPath(outputDir, stem, createSubDirs);
        // The row still represents one logical texture, so the extraction
        // count remains one. Stored lower-resolution levels are written as
        // deterministic _mipN companions beside the historical top-level PNG.
        N64TextureOutput.WritePngLevels(texture, outPath);
        return (1, 1, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractGbaImages(
        byte[] data,
        string outputDir,
        string stem)
    {
        var images = GbaRomImages.ScanFullScreenImages(data);
        if (images.Count == 0)
            return (0, 0, false, false);

        // A ROM yields many anonymous screens; group them under the ROM's own
        // folder so distinct carts extracted together never collide. (.gba is a
        // simple extension the compound-suffix stripper leaves on the stem.)
        var dir = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(stem));
        Directory.CreateDirectory(dir);
        var written = 0;
        foreach (var image in images)
        {
            ImageWriter.WritePng(
                Path.Combine(dir, image.Name + ".png"),
                GbaRomImages.ScreenWidth, GbaRomImages.ScreenHeight, image.Rgba);
            written++;
        }

        return (images.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractPsxTextures(
        byte[] data,
        string label,
        string outputDir,
        bool createSubDirs,
        bool writeDds,
        bool writeMipAtlas)
    {
        var result = PsxLibrary.ExtractTextures(data, label, outputDir, createSubDirs, writeDds, writeMipAtlas);
        return (result.TotalTextures, result.TexturesWritten, result.Skipped, result.Success);
    }

    private static string BuildSingleTextureOutputPath(string outputDir, string stem, bool createSubDirs)
    {
        var outPath = createSubDirs
            ? Path.Combine(outputDir, stem, stem + ".png")
            : Path.Combine(outputDir, stem + ".png");

        if (createSubDirs)
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);

        return outPath;
    }

    private static (byte[] rgba, int width, int height)? GetPreviewTexture(Ps2TexResult result, int textureIndex)
    {
        return TexturePreviewSelector.Select(result, textureIndex);
    }

    private static Ps2TexResult ParseXbxTextures(byte[] data, TextureFileFormat format)
    {
        return ParseXbxTextures(data, format, out _);
    }

    private static Ps2TexResult ParseXbxTextures(
        byte[] data,
        TextureFileFormat format,
        out string formatLabel)
    {
        if (format == TextureFileFormat.XbxImg)
        {
            formatLabel = "Xbox IMG";
            var result = XbxImgFile.Parse(data);
            if (result.Success)
                return result;

            formatLabel = "THAW PC IMG";
            return ThawImgFile.Parse(data);
        }

        formatLabel = "Xbox TEX";
        var texResult = XbxTexFile.Parse(data);
        if (texResult.Success)
            return texResult;

        var thawResult = ThawTexFile.Parse(data);
        if (thawResult.Success)
        {
            formatLabel = "THAW PC TEX";
            return thawResult;
        }

        // THAW PS2 .stex zone textures (per-file decode; the CLI merges VRAM
        // across a zone's files, but every texture also decodes standalone).
        if (ThawZoneTexFile.IsThawZoneTex(data))
        {
            var textures = ThawZoneTexFile.DecodeAllFromFile(data);
            if (textures.Count > 0)
            {
                formatLabel = "THAW Zone TEX (PS2)";
                return new Ps2TexResult(textures);
            }
        }

        return thawResult;
    }

    private static string StripCompoundExtension(string filename)
    {
        return OrdinalFileName.StripCompoundSuffix(filename, CompoundTextureExtensions);
    }
}
