using System.CommandLine;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Texture;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene.ZoneTex;
using NeversoftMultitool.Core.Formats.Texture.XbxScene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace ThawZoneTexAnalyzer.Commands;

/// <summary>
///     Reads THAW PS2 STEX payloads through ArchiveFileSystem (WAD -&gt; nested PAK),
///     compares every decoder path, writes PNGs, and reports alpha/layout metrics.
///     This deliberately never consumes an extracted Sample/Builds PAK subtree.
/// </summary>
internal static class ArchiveStexDiagnosticsCommand
{
    private static readonly string[] DefaultPakPaths =
    [
        "worlds/worldzones/z_sz/z_szped.pak.ps2",
        "cutscenes/bh_levelevent/ps2/bh_levelevent_main/bh_levelevent_main.pak.ps2",
        "pak/cagr_assets/cagr_assets_g.pak.ps2"
    ];

    public static Command Create()
    {
        var wadOption = new Option<string>("--wad")
        {
            Description = "Path to the shipped THAW PS2 DATAP.WAD."
        };
        wadOption.Required = true;

        var pakOption = new Option<string[]>("--pak")
        {
            Description = "WAD-relative nested PAK path. Defaults to the three reported archives.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        var entryOption = new Option<string[]>("--entry")
        {
            Description = "Nested STEX basename filter (for example 0003B210.stex).",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true
        };
        var outputOption = new Option<string>("--output")
        {
            Description = "Directory for PNG variants and the CSV report.",
            DefaultValueFactory = _ => Path.Combine("TestOutput", "thaw_archive_stex_diagnostics")
        };
        var referencePakOption = new Option<string[]>("--reference-pak")
        {
            Description = "PC/other-platform PAKs to scan directly for checksum-matched reference textures.",
            Arity = ArgumentArity.ZeroOrMore,
            AllowMultipleArgumentsPerToken = true
        };

        var command = new Command("archive-stex-diagnostics",
            "Diagnose PS2 STEX files directly through DATAP.WAD and nested PAK ArchiveFs layers.");
        command.Options.Add(wadOption);
        command.Options.Add(pakOption);
        command.Options.Add(entryOption);
        command.Options.Add(outputOption);
        command.Options.Add(referencePakOption);
        command.SetAction((parseResult, _) =>
        {
            var wad = parseResult.GetRequiredValue(wadOption);
            var paks = parseResult.GetValue(pakOption) ?? [];
            var entries = parseResult.GetValue(entryOption) ?? [];
            var output = parseResult.GetValue(outputOption) ??
                         Path.Combine("TestOutput", "thaw_archive_stex_diagnostics");
            var referencePaks = parseResult.GetValue(referencePakOption) ?? [];
            return Task.FromResult(Run(wad, paks, entries, output, referencePaks));
        });
        return command;
    }

    private static int Run(
        string wadPath,
        string[] pakPaths,
        string[] entryFilters,
        string outputDirectory,
        string[] referencePakPaths)
    {
        Directory.CreateDirectory(outputDirectory);
        using var wad = ArchiveFileSystem.TryOpen(Path.GetFullPath(wadPath));
        if (wad == null)
        {
            Console.Error.WriteLine($"Could not open WAD: {wadPath}");
            return 1;
        }

        var requestedPaks = pakPaths.Length == 0 ? DefaultPakPaths : pakPaths;
        var requestedEntries = entryFilters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var references = LoadReferenceTextures(referencePakPaths);
        var reportRows = new List<string>
        {
            "pak,entry,sha256,file_bytes,records,record_index,checksum,psm,width,height,decoder,rgba_sha256,alpha_min,alpha_max,alpha_distinct,alpha_zero,alpha_partial,alpha_opaque,luma_entropy,repeat_x_8,repeat_y_8,reference_rgb_mad,reference_alpha_mad"
        };

        foreach (var pakPath in requestedPaks)
        {
            var pakEntry = FindPak(wad, pakPath);
            if (pakEntry == null)
            {
                Console.Error.WriteLine($"PAK not found in WAD: {pakPath}");
                continue;
            }

            using var pak = wad.TryOpenNested(pakEntry);
            if (pak == null)
            {
                Console.Error.WriteLine($"Could not open nested PAK: {pakEntry.FullName}");
                continue;
            }

            var stexEntries = pak.Entries
                .Where(static entry => entry.Name.EndsWith(".stex", StringComparison.OrdinalIgnoreCase))
                .Where(entry => requestedEntries.Count == 0 || requestedEntries.Contains(entry.Name))
                .OrderBy(static entry => entry.Offset)
                .ToList();
            Console.WriteLine($"{pakEntry.FullName}: {stexEntries.Count} selected STEX / {pak.Entries.Count} entries");

            foreach (var stexEntry in stexEntries)
                DiagnoseEntry(pakEntry, pak, stexEntry, outputDirectory, reportRows, references);
        }

        var reportPath = Path.Combine(outputDirectory, "archive_stex_metrics.csv");
        File.WriteAllLines(reportPath, reportRows);
        Console.WriteLine($"Wrote {reportRows.Count - 1} metric rows to {reportPath}");
        return 0;
    }

    private static Dictionary<uint, Ps2Texture> LoadReferenceTextures(IEnumerable<string> pakPaths)
    {
        var references = new Dictionary<uint, Ps2Texture>();
        foreach (var pakPath in pakPaths)
        {
            using var pak = ArchiveFileSystem.TryOpen(Path.GetFullPath(pakPath));
            if (pak == null)
            {
                Console.Error.WriteLine($"Reference PAK could not be opened: {pakPath}");
                continue;
            }

            var parsedEntries = 0;
            foreach (var entry in pak.Entries)
            {
                byte[] data;
                try
                {
                    data = pak.ReadEntry(entry);
                }
                catch (InvalidDataException)
                {
                    continue;
                }

                var result = ThawImgFile.Parse(data);
                if (!result.Success)
                    result = ThawTexFile.Parse(data);
                if (!result.Success)
                    continue;

                parsedEntries++;
                foreach (var texture in result.Textures)
                {
                    if (texture.Pixels != null)
                        references.TryAdd(texture.Checksum, texture);
                }
            }

            Console.WriteLine(
                $"Reference {pak.DisplayPath}: {references.Count} checksum textures after {parsedEntries}/{pak.Entries.Count} parsed entries");
        }

        return references;
    }

    private static ArchiveEntry? FindPak(IArchiveFileSystem wad, string requestedPath)
    {
        return wad.FindByPath(requestedPath)
               ?? wad.Entries.FirstOrDefault(entry =>
                   entry.FullName.EndsWith(requestedPath.Replace('\\', '/'),
                       StringComparison.OrdinalIgnoreCase));
    }

    private static void DiagnoseEntry(
        ArchiveEntry pakEntry,
        IArchiveFileSystem pak,
        ArchiveEntry stexEntry,
        string outputDirectory,
        List<string> reportRows,
        IReadOnlyDictionary<uint, Ps2Texture> references)
    {
        var data = pak.ReadEntry(stexEntry);
        var dataHash = Convert.ToHexString(SHA256.HashData(data));
        var headers = ThawZoneTexFile.ParseHeaderEntries(data);
        var uploads = ThawZoneTexFile.ParseVramUploads(data);
        var current = ThawZoneTexFile.DecodeAllFromFile(data);
        var owner = ThawZoneTexOwnerBlobDecoder.DecodeAllRecords(data, headers);
        var legacy = ThawZoneTexFile.DecodeFromHeaderDataSlots(data, uploads, headers);
        var upload = ThawZoneTexFile.DecodeFromHeaderEntries(uploads, headers);
        var direct = headers
            .Select(header => ThawZoneTexCoreDecoder.DecodeRecord(data, header))
            .WhereNotNull()
            .ToList();

        var ownerHeader = ThawZoneTexOwnerBlobDecoder.TryFindOwnerBlobHeader(
            data, out var ownerOffset, out var primaryCount, out var secondaryCount,
            out var baseA, out var baseB, out var dmaStart)
            ? $"owner=0x{ownerOffset:X}/p{primaryCount}/s{secondaryCount}/a0x{baseA:X}/b0x{baseB:X}/dma0x{dmaStart:X}"
            : "owner=none";
        Console.WriteLine(
            $"  {stexEntry.Name}: bytes={data.Length:N0} sha256={dataHash[..16]} records={headers.Count} uploads={uploads.Count} " +
            $"current={current.Count} owner={owner.Count} legacy={legacy.Count} upload={upload.Count} direct={direct.Count} {ownerHeader}");

        var archiveStem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(pakEntry.Name));
        var entryStem = Path.GetFileNameWithoutExtension(stexEntry.Name);
        var entryOutput = Path.Combine(outputDirectory, archiveStem, entryStem);
        Directory.CreateDirectory(entryOutput);

        var variants = new Dictionary<string, IReadOnlyList<Ps2Texture>>(StringComparer.Ordinal)
        {
            ["current"] = current,
            ["owner"] = owner,
            ["legacy"] = legacy,
            ["upload"] = upload,
            ["direct"] = direct
        };
        var matchedReferences = headers.Select(static header => header.Checksum)
            .Distinct()
            .Select(checksum => references.GetValueOrDefault(checksum))
            .WhereNotNull()
            .ToList();
        if (matchedReferences.Count > 0)
            variants["reference"] = matchedReferences;

        foreach (var (decoder, textures) in variants)
        {
            var textureMap = textures.GroupBy(static texture => texture.Checksum)
                .ToDictionary(static group => group.Key, static group => group.First());
            for (var recordIndex = 0; recordIndex < headers.Count; recordIndex++)
            {
                var header = headers[recordIndex];
                if (!textureMap.TryGetValue(header.Checksum, out var texture) || texture.Pixels == null)
                    continue;

                var imagePath = Path.Combine(entryOutput,
                    $"{recordIndex:D4}_{header.Checksum:X8}_{decoder}_{texture.Width}x{texture.Height}.png");
                using var image = Image.LoadPixelData<Rgba32>(texture.Pixels, texture.Width, texture.Height);
                image.SaveAsPng(imagePath);

                var metrics = Measure(texture);
                var referenceMad = references.TryGetValue(header.Checksum, out var reference)
                    ? ComputeReferenceMad(texture, reference)
                    : null;
                reportRows.Add(string.Join(',',
                    Csv(pakEntry.FullName), Csv(stexEntry.Name), dataHash, data.Length, headers.Count, recordIndex,
                    $"{header.Checksum:X8}", texture.Psm, texture.Width, texture.Height, decoder,
                    metrics.RgbaHash, metrics.AlphaMin, metrics.AlphaMax, metrics.AlphaDistinct,
                    metrics.AlphaZero, metrics.AlphaPartial, metrics.AlphaOpaque,
                    metrics.LumaEntropy.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    metrics.RepeatX8.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    metrics.RepeatY8.ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                    referenceMad?.Rgb.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? "",
                    referenceMad?.Alpha.ToString("F6", System.Globalization.CultureInfo.InvariantCulture) ?? ""));
            }
        }
    }

    private static (double Rgb, double Alpha)? ComputeReferenceMad(Ps2Texture texture, Ps2Texture reference)
    {
        if (texture.Pixels == null || reference.Pixels == null
                                   || texture.Width != reference.Width || texture.Height != reference.Height)
            return null;

        long rgbDifference = 0;
        long alphaDifference = 0;
        for (var offset = 0; offset < texture.Pixels.Length; offset += 4)
        {
            rgbDifference += Math.Abs(texture.Pixels[offset] - reference.Pixels[offset]);
            rgbDifference += Math.Abs(texture.Pixels[offset + 1] - reference.Pixels[offset + 1]);
            rgbDifference += Math.Abs(texture.Pixels[offset + 2] - reference.Pixels[offset + 2]);
            alphaDifference += Math.Abs(texture.Pixels[offset + 3] - reference.Pixels[offset + 3]);
        }

        var pixels = texture.Width * texture.Height;
        return (rgbDifference / (pixels * 3.0), alphaDifference / (double)pixels);
    }

    private static TextureMetrics Measure(Ps2Texture texture)
    {
        var alphaHistogram = new int[256];
        var lumaHistogram = new int[256];
        var pixels = texture.Pixels!;
        long repeatX = 0;
        long repeatY = 0;
        long repeatXCount = 0;
        long repeatYCount = 0;
        for (var y = 0; y < texture.Height; y++)
        for (var x = 0; x < texture.Width; x++)
        {
            var offset = (y * texture.Width + x) * 4;
            var r = pixels[offset];
            var g = pixels[offset + 1];
            var b = pixels[offset + 2];
            alphaHistogram[pixels[offset + 3]]++;
            lumaHistogram[(r * 54 + g * 183 + b * 19) >> 8]++;

            if (x >= 8)
            {
                repeatX += RgbaDifference(pixels, offset, offset - 8 * 4);
                repeatXCount += 4;
            }
            if (y >= 8)
            {
                repeatY += RgbaDifference(pixels, offset, offset - 8 * texture.Width * 4);
                repeatYCount += 4;
            }
        }

        var pixelCount = texture.Width * texture.Height;
        var partialAlpha = 0;
        var alphaMin = 0;
        var alphaMax = 255;
        var alphaDistinct = 0;
        for (var index = 0; index < 256; index++)
        {
            if (alphaHistogram[index] == 0)
                continue;
            if (alphaDistinct == 0)
                alphaMin = index;
            alphaMax = index;
            alphaDistinct++;
            if (index is > 0 and < 255)
                partialAlpha += alphaHistogram[index];
        }
        var entropy = 0.0;
        foreach (var count in lumaHistogram)
        {
            if (count == 0) continue;
            var probability = (double)count / pixelCount;
            entropy -= probability * Math.Log2(probability);
        }

        return new TextureMetrics(
            Convert.ToHexString(SHA256.HashData(pixels)),
            alphaMin, alphaMax, alphaDistinct,
            alphaHistogram[0], partialAlpha, alphaHistogram[255], entropy,
            repeatXCount == 0 ? 0 : repeatX / (repeatXCount * 255.0),
            repeatYCount == 0 ? 0 : repeatY / (repeatYCount * 255.0));
    }

    private static int RgbaDifference(byte[] pixels, int left, int right)
    {
        return Math.Abs(pixels[left] - pixels[right])
               + Math.Abs(pixels[left + 1] - pixels[right + 1])
               + Math.Abs(pixels[left + 2] - pixels[right + 2])
               + Math.Abs(pixels[left + 3] - pixels[right + 3]);
    }

    private static string Csv(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

    private readonly record struct TextureMetrics(
        string RgbaHash,
        int AlphaMin,
        int AlphaMax,
        int AlphaDistinct,
        int AlphaZero,
        int AlphaPartial,
        int AlphaOpaque,
        double LumaEntropy,
        double RepeatX8,
        double RepeatY8);

    private static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T?> source) where T : class
    {
        foreach (var item in source)
        {
            if (item != null)
                yield return item;
        }
    }
}
