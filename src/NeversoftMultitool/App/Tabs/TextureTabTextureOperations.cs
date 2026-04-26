using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
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

    public static int CountTextures(AssetSource source, TextureFileFormat format)
    {
        var data = source.ReadBytes();
        return format switch
        {
            TextureFileFormat.Ps2Tex => CountParsedTextures(Ps2TexFile.Parse(data)),
            TextureFileFormat.NgcTex => CountParsedTextures(NgcTexFile.Parse(data)),
            TextureFileFormat.XbxTex => CountParsedTextures(ParseXbxTextures(data, format)),
            TextureFileFormat.XbxImg => CountParsedTextures(ParseXbxTextures(data, format)),
            TextureFileFormat.Pvr => PvrFileDecoder.DecodeToRgba(data) != null ? 1 : 0,
            _ => PsxLibrary.EnumerateTextures(data).Count
        };
    }

    public static List<PsxTextureEntry> EnumerateChildren(
        AssetSource source,
        string parentFileName,
        TextureFileFormat format)
    {
        var data = source.ReadBytes();
        return format switch
        {
            TextureFileFormat.Ps2Tex => BuildPs2Entries(Ps2TexFile.Parse(data), parentFileName),
            TextureFileFormat.NgcTex => BuildNgcEntries(NgcTexFile.Parse(data), parentFileName),
            TextureFileFormat.XbxTex => BuildXboxEntries(ParseXbxTextures(data, format), parentFileName, format),
            TextureFileFormat.XbxImg => BuildXboxEntries(ParseXbxTextures(data, format), parentFileName, format),
            TextureFileFormat.Pvr => BuildPvrEntries(data, parentFileName),
            _ => BuildPsxEntries(data, parentFileName)
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
            _ => ExtractPsxTextures(data, entry.FileName, outputDir, createSubDirs, writeDds, writeMipAtlas)
        };
    }

    public static (byte[] rgba, int width, int height)? GetPreviewRgba(
        AssetSource source,
        uint nameHash,
        TextureFileFormat format)
    {
        var data = source.ReadBytes();
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
                return GetPreviewTexture(Ps2TexFile.Parse(data), nameHash);
            case TextureFileFormat.NgcTex:
                return GetPreviewTexture(NgcTexFile.Parse(data), nameHash);
            case TextureFileFormat.XbxTex:
                return GetPreviewTexture(ParseXbxTextures(data, format), nameHash);
            case TextureFileFormat.XbxImg:
            {
                var result = ParseXbxTextures(data, format);
                if (!result.Success)
                    return null;

                var tex = result.Textures.FirstOrDefault(t => t.Pixels != null);
                return tex?.Pixels != null ? (tex.Pixels, tex.Width, tex.Height) : null;
            }
            case TextureFileFormat.Pvr:
                return PvrFileDecoder.DecodeToRgba(data);
            default:
                return PsxLibrary.ExtractTextureByHash(data, nameHash, source.EntryName);
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

    private static List<PsxTextureEntry> BuildPvrEntries(byte[] data, string parentFileName)
    {
        var pvr = PvrFileDecoder.DecodeToRgba(data);
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

    private static List<PsxTextureEntry> BuildPsxEntries(byte[] data, string parentFileName)
    {
        return PsxLibrary.EnumerateTextures(data)
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
        byte[] data,
        string outputDir,
        string stem)
    {
        var result = Ps2TexFile.Parse(data);
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

    private static (byte[] rgba, int width, int height)? GetPreviewTexture(Ps2TexResult result, uint nameHash)
    {
        if (!result.Success)
            return null;

        var texture = result.Textures.FirstOrDefault(item => item.Checksum == nameHash && item.Pixels != null);
        return texture?.Pixels != null ? (texture.Pixels, texture.Width, texture.Height) : null;
    }

    private static Ps2TexResult ParseXbxTextures(byte[] data, TextureFileFormat format)
    {
        if (format == TextureFileFormat.XbxImg)
        {
            var result = XbxImgFile.Parse(data);
            return result.Success ? result : ThawImgFile.Parse(data);
        }

        var texResult = XbxTexFile.Parse(data);
        return texResult.Success ? texResult : ThawTexFile.Parse(data);
    }

    private static string StripCompoundExtension(string filename)
    {
        return OrdinalFileName.StripCompoundSuffix(filename, CompoundTextureExtensions);
    }
}
