using NeversoftMultitool.Core.BinaryIO;

namespace NeversoftMultitool.Core.Formats.Font;

/// <summary>
///     Writes the two artifacts a converted <c>.fnt</c> produces: one packed glyph atlas and
///     one metrics manifest. The CLI and the GUI both route through here so their output stays
///     identical.
/// </summary>
public static class FntOutput
{
    /// <summary>Suffix appended to the stem for the glyph atlas.</summary>
    public const string AtlasSuffix = ".fnt.png";

    /// <summary>Suffix appended to the stem for the metrics manifest.</summary>
    public const string ManifestSuffix = ".fnt.json";

    /// <summary>Converts one font, returning the paths written in write order.</summary>
    public static IReadOnlyList<string> Write(
        string outputDirectory,
        string stem,
        string sourceFile,
        FntFile font,
        FntCharacterMap.Mode? characterMapMode = null,
        int maxAtlasWidth = FntAtlasBuilder.DefaultMaxWidth,
        int padding = FntAtlasBuilder.DefaultPadding)
    {
        ArgumentException.ThrowIfNullOrEmpty(stem);
        ArgumentNullException.ThrowIfNull(font);

        var atlas = FntAtlasBuilder.Build(font, maxAtlasWidth, padding);
        var atlasFileName = stem + AtlasSuffix;
        var atlasPath = Path.Combine(outputDirectory, atlasFileName);
        var manifestPath = Path.Combine(outputDirectory, stem + ManifestSuffix);

        // Materialize the manifest before touching the filesystem, so a serialization failure
        // cannot leave an atlas on disk with no metrics beside it.
        var manifest = FntJsonExporter.Serialize(sourceFile, font, atlas, atlasFileName, characterMapMode);

        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        ImageWriter.WritePng(atlasPath, atlas.Width, atlas.Height, atlas.Rgba);
        File.WriteAllText(manifestPath, manifest);

        return [atlasPath, manifestPath];
    }
}
