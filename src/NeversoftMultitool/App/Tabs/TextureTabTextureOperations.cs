using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Texture.Ngc;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Psx;
using NeversoftMultitool.Core.Formats.Texture.Pvr;
using NeversoftMultitool.Core.Formats.XbxScene;
using NeversoftMultitool.Core.QbKey;

namespace NeversoftMultitool;

internal static class TextureTabTextureOperations
{
    private static readonly string[] CompoundTextureExtensions =
    [
        ".tex.xbx", ".img.xbx", ".tex.wpc", ".img.wpc",
        ".tex.ps2", ".img.ps2", ".tex.ngc"
    ];

    private static readonly string[] NgcTexExtensions = [".tex.ngc"];
    private static readonly string[] XboxTexExtensions = [".tex.xbx", ".tex.wpc"];
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
               || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    public static TextureFileFormat ClassifyFormat(string fileName)
    {
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

    public static int CountTextures(string inputFile, TextureFileFormat format)
    {
        return format switch
        {
            TextureFileFormat.Ps2Tex => CountParsedTextures(Ps2TexFile.Parse(inputFile)),
            TextureFileFormat.NgcTex => CountParsedTextures(NgcTexFile.Parse(inputFile)),
            TextureFileFormat.XbxTex => CountParsedTextures(ParseXbxTextures(inputFile, format)),
            TextureFileFormat.XbxImg => CountParsedTextures(ParseXbxTextures(inputFile, format)),
            TextureFileFormat.Pvr => PvrFileDecoder.DecodeToRgba(inputFile) != null ? 1 : 0,
            _ => PsxLibrary.EnumerateTextures(inputFile).Count
        };
    }

    public static List<PsxTextureEntry> EnumerateChildren(
        string inputFile,
        string parentFileName,
        TextureFileFormat format)
    {
        return format switch
        {
            TextureFileFormat.Ps2Tex => BuildPs2Entries(Ps2TexFile.Parse(inputFile), parentFileName),
            TextureFileFormat.NgcTex => BuildNgcEntries(NgcTexFile.Parse(inputFile), parentFileName),
            TextureFileFormat.XbxTex => BuildXboxEntries(ParseXbxTextures(inputFile, format), parentFileName, format),
            TextureFileFormat.XbxImg => BuildXboxEntries(ParseXbxTextures(inputFile, format), parentFileName, format),
            TextureFileFormat.Pvr => BuildPvrEntries(inputFile, parentFileName),
            _ => BuildPsxEntries(inputFile, parentFileName)
        };
    }

    public static (int totalTex, int writtenTex, bool skipped, bool success) ExtractTextures(
        string inputFile,
        PsxFileEntry entry,
        string outputDir,
        bool createSubDirs,
        bool writeDds,
        bool writeMipAtlas)
    {
        var stem = StripCompoundExtension(entry.FileName);

        return entry.Format switch
        {
            TextureFileFormat.Ps2Tex => ExtractPs2Textures(inputFile, outputDir, stem),
            TextureFileFormat.NgcTex => ExtractNgcTextures(inputFile, outputDir, stem),
            TextureFileFormat.XbxTex => ExtractXbxTextures(inputFile, outputDir, stem, entry.Format),
            TextureFileFormat.XbxImg => ExtractXbxImage(inputFile, outputDir, stem, createSubDirs),
            TextureFileFormat.Pvr => ExtractPvr(inputFile, outputDir, stem, createSubDirs),
            _ => ExtractPsxTextures(inputFile, outputDir, createSubDirs, writeDds, writeMipAtlas)
        };
    }

    public static (byte[] rgba, int width, int height)? GetPreviewRgba(
        string inputFile,
        uint nameHash,
        TextureFileFormat format)
    {
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
                return GetPreviewTexture(Ps2TexFile.Parse(inputFile), nameHash);
            case TextureFileFormat.NgcTex:
                return GetPreviewTexture(NgcTexFile.Parse(inputFile), nameHash);
            case TextureFileFormat.XbxTex:
                return GetPreviewTexture(ParseXbxTextures(inputFile, format), nameHash);
            case TextureFileFormat.XbxImg:
            {
                var result = ParseXbxTextures(inputFile, format);
                if (!result.Success)
                    return null;

                var tex = result.Textures.FirstOrDefault(t => t.Pixels != null);
                return tex?.Pixels != null ? (tex.Pixels, tex.Width, tex.Height) : null;
            }
            case TextureFileFormat.Pvr:
                return PvrFileDecoder.DecodeToRgba(inputFile);
            default:
                return PsxLibrary.ExtractTextureByHash(inputFile, nameHash);
        }
    }

    private static int CountParsedTextures(Ps2TexResult result)
    {
        return result.Success ? result.Textures.Count(texture => texture.Pixels != null) : 0;
    }

    private static List<PsxTextureEntry> BuildPs2Entries(Ps2TexResult result, string parentFileName)
    {
        return result.Success
            ? result.Textures.Where(texture => texture.Pixels != null)
                .Select((texture, index) => new PsxTextureEntry
                {
                    ParentFileName = parentFileName,
                    NameHash = texture.Checksum,
                    Width = texture.Width,
                    Height = texture.Height,
                    PaletteType = Ps2TexFile.DescribePsm(texture.Psm),
                    Index = index,
                    ResolvedName = texture.Name ?? QbKey.TryResolve(texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildNgcEntries(Ps2TexResult result, string parentFileName)
    {
        return result.Success
            ? result.Textures.Where(texture => texture.Pixels != null)
                .Select((texture, index) => new PsxTextureEntry
                {
                    ParentFileName = parentFileName,
                    NameHash = texture.Checksum,
                    Width = texture.Width,
                    Height = texture.Height,
                    PaletteType = "NGC TEX",
                    Index = index,
                    ResolvedName = texture.Name ?? QbKey.TryResolve(texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildXboxEntries(
        Ps2TexResult result,
        string parentFileName,
        TextureFileFormat format)
    {
        return result.Success
            ? result.Textures.Where(texture => texture.Pixels != null)
                .Select((texture, index) => new PsxTextureEntry
                {
                    ParentFileName = parentFileName,
                    NameHash = texture.Checksum,
                    Width = texture.Width,
                    Height = texture.Height,
                    PaletteType = format == TextureFileFormat.XbxImg ? "Xbox IMG" : "Xbox TEX",
                    Index = index,
                    ResolvedName = texture.Name ?? QbKey.TryResolve(texture.Checksum)
                })
                .ToList()
            : [];
    }

    private static List<PsxTextureEntry> BuildPvrEntries(string inputFile, string parentFileName)
    {
        var pvr = PvrFileDecoder.DecodeToRgba(inputFile);
        return pvr != null
            ?
            [
                new PsxTextureEntry
                {
                    ParentFileName = parentFileName,
                    NameHash = 0,
                    Width = pvr.Value.Width,
                    Height = pvr.Value.Height,
                    PaletteType = "PVR",
                    Index = 0,
                    ResolvedName = Path.GetFileNameWithoutExtension(parentFileName)
                }
            ]
            : [];
    }

    private static List<PsxTextureEntry> BuildPsxEntries(string inputFile, string parentFileName)
    {
        return PsxLibrary.EnumerateTextures(inputFile)
            .Select((texture, index) => new PsxTextureEntry
            {
                ParentFileName = parentFileName,
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
        string inputFile,
        string outputDir,
        string stem)
    {
        var result = Ps2TexFile.Parse(inputFile);
        if (!result.Success)
            return (0, 0, false, false);

        var written = Ps2TexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractNgcTextures(
        string inputFile,
        string outputDir,
        string stem)
    {
        var result = NgcTexFile.Parse(inputFile);
        if (!result.Success)
            return (0, 0, false, false);

        var written = NgcTexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractXbxTextures(
        string inputFile,
        string outputDir,
        string stem,
        TextureFileFormat format)
    {
        var result = ParseXbxTextures(inputFile, format);
        if (!result.Success)
            return (0, 0, false, false);

        var written = XbxTexFile.SaveAllAsPng(result, outputDir, stem);
        return (result.Textures.Count, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractXbxImage(
        string inputFile,
        string outputDir,
        string stem,
        bool createSubDirs)
    {
        var result = ParseXbxTextures(inputFile, TextureFileFormat.XbxImg);
        if (!result.Success)
            return (0, 0, false, false);

        var outPath = BuildSingleTextureOutputPath(outputDir, stem, createSubDirs);
        var written = XbxImgFile.SaveAsPng(result, outPath);
        return (1, written, false, true);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractPvr(
        string inputFile,
        string outputDir,
        string stem,
        bool createSubDirs)
    {
        var outPath = BuildSingleTextureOutputPath(outputDir, stem, createSubDirs);
        using var stream = File.OpenRead(inputFile);
        using var reader = new BinaryReader(stream);
        var ok = PvrFileDecoder.DecodeToPng(reader, 0, outPath);
        return (1, ok ? 1 : 0, false, ok);
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractPsxTextures(
        string inputFile,
        string outputDir,
        bool createSubDirs,
        bool writeDds,
        bool writeMipAtlas)
    {
        var result = PsxLibrary.ExtractTextures(inputFile, outputDir, createSubDirs, writeDds, writeMipAtlas);
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

    private static (byte[] rgba, int width, int height)? GetPreviewTexture(Ps2TexResult result, uint nameHash)
    {
        if (!result.Success)
            return null;

        var texture = result.Textures.FirstOrDefault(item => item.Checksum == nameHash && item.Pixels != null);
        return texture?.Pixels != null ? (texture.Pixels, texture.Width, texture.Height) : null;
    }

    private static Ps2TexResult ParseXbxTextures(string inputFile, TextureFileFormat format)
    {
        if (format == TextureFileFormat.XbxImg)
        {
            var result = XbxImgFile.Parse(inputFile);
            return result.Success ? result : ThawImgFile.Parse(inputFile);
        }

        var texResult = XbxTexFile.Parse(inputFile);
        return texResult.Success ? texResult : ThawTexFile.Parse(inputFile);
    }

    private static string StripCompoundExtension(string filename)
    {
        return OrdinalFileName.StripCompoundSuffix(filename, CompoundTextureExtensions);
    }
}
