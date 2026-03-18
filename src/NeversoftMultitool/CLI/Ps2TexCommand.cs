using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class Ps2TexCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PS2 TEX/IMG file or directory containing them"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted PNG files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };
        var gifQwordOrderOption = new Option<string>("--gif-qword-order")
        {
            Description =
                "Diagnostic: reorder 32-bit words inside each 16-byte GIF IMAGE qword before CT32/CT16 writes. Use a 4-digit permutation such as 0123 or 2031.",
            DefaultValueFactory = _ => "0123"
        };

        var command = new Command("ps2tex", "Extract textures from PS2 TEX/IMG files to PNG");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);
        command.Options.Add(gifQwordOrderOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);
            var gifQwordOrderText = parseResult.GetValue(gifQwordOrderOption)!;

            return Task.FromResult(Execute(input, output, verbose, gifQwordOrderText));
        });

        return command;
    }

    private static int Execute(string input, string output, bool verbose, string gifQwordOrderText)
    {
        if (!Ps2GifQwordWordOrder.TryParse(gifQwordOrderText, out var gifQwordWordOrder))
        {
            AnsiConsole.MarkupLine(
                $"[red]Error:[/] Invalid GIF qword order '{Markup.Escape(gifQwordOrderText)}'. Expected a 4-digit permutation of 0-3, for example 0123 or 2031.");
            return 1;
        }

        List<string> files;

        if (File.Exists(input))
        {
            files = [input];
        }
        else if (Directory.Exists(input))
        {
            files = Directory.GetFiles(input, "*.*", SearchOption.AllDirectories)
                .Where(IsPs2TextureFile)
                .ToList();
        }
        else
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Path not found: {input}");
            return 1;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No TEX/IMG files found.[/]");
            return 0;
        }

        // Probe for unsupported files
        var (supported, unsupported) = FormatProbe.PartitionFiles(files, FormatProbe.ProbeTexture);
        if (unsupported.Count > 0)
        {
            AnsiConsole.MarkupLine(
                $"Found [green]{files.Count}[/] files " +
                $"([green]{supported.Count}[/] supported, [yellow]{unsupported.Count}[/] unsupported)");
            foreach (var (fileName, reason) in unsupported)
                AnsiConsole.MarkupLine($"  [yellow]\u26a0[/] {Markup.Escape(fileName)}: {Markup.Escape(reason)}");
            files = supported;
        }

        if (files.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No supported TEX/IMG files to process.[/]");
            return 0;
        }

        Directory.CreateDirectory(output);

        // Separate zone TEX files from standard TEX/IMG files.
        // Zone TEX files must be processed together (merged VRAM map) because
        // textures can reference pixel data from one file and CLUT data from another.
        var standardFiles = new List<string>();
        var zoneTexFiles = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var data = File.ReadAllBytes(file);
                if (ThawZoneTexFile.IsThawZoneTex(data))
                    zoneTexFiles.Add(file);
                else
                    standardFiles.Add(file);
            }
            catch
            {
                standardFiles.Add(file);
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var converted = 0;
        var failed = 0;
        var totalTextures = 0;

        // Process zone TEX files as a batch with merged VRAM map
        if (zoneTexFiles.Count > 0)
        {
            var zoneResult = ProcessZoneTexFiles(zoneTexFiles, output, verbose, gifQwordWordOrder);
            totalTextures += zoneResult.Textures;
            if (zoneResult.Textures > 0) converted += zoneResult.FilesConverted;
            failed += zoneResult.FilesFailed;
        }

        // Process standard TEX/IMG files individually
        if (standardFiles.Count > 0)
            AnsiConsole.MarkupLine($"Processing [green]{standardFiles.Count}[/] standard TEX/IMG file(s)");

        foreach (var file in standardFiles)
        {
            var filename = Path.GetFileName(file);
            var stem = Path.GetFileNameWithoutExtension(filename);
            if (stem.EndsWith(".tex", StringComparison.OrdinalIgnoreCase) ||
                stem.EndsWith(".img", StringComparison.OrdinalIgnoreCase))
                stem = stem[..^4];

            var result = Ps2TexFile.Parse(file);
            if (!result.Success)
                result = ThawSceneTexFile.Parse(file);

            if (!result.Success)
            {
                failed++;
                if (verbose)
                    AnsiConsole.MarkupLine($"  {filename}: [red]{result.ErrorMessage}[/]");
                continue;
            }

            var count = Ps2TexFile.SaveAllAsPng(result, output, stem);
            totalTextures += count;
            converted++;

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {filename}: [green]{result.Textures.Count} textures, {count} PNGs[/]");
            }
        }

        stopwatch.Stop();
        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/]/{files.Count} files " +
            $"({totalTextures:N0} textures, {failed} failed) " +
            $"in {stopwatch.Elapsed.TotalSeconds:F2}s");

        return 0;
    }

    /// <summary>
    ///     Process all zone TEX files together: merge VRAM maps across all files,
    ///     collect TEX0 values from all companion MDL files, then decode once.
    ///     This is necessary because textures can reference pixel data from one
    ///     zone TEX and CLUT data from another.
    /// </summary>
    private static ZoneTexBatchResult ProcessZoneTexFiles(
        List<string> zoneTexFiles, string output, bool verbose, Ps2GifQwordWordOrder gifQwordWordOrder)
    {
        AnsiConsole.MarkupLine(
            $"Processing [green]{zoneTexFiles.Count}[/] THAW zone TEX file(s) (merged VRAM)");
        if (!gifQwordWordOrder.IsIdentity)
            AnsiConsole.MarkupLine($"  GIF qword word order: [green]{gifQwordWordOrder}[/]");

        // Merge VRAM maps from all zone TEX files
        var mergedUploads = new List<ThawZoneTexFile.VramUpload>();
        var zoneSources = new List<(ReadOnlyMemory<byte> FileData, List<ThawZoneTexFile.VramUpload> Uploads,
            List<ThawZoneTexFile.ZoneTexHeaderEntry> Headers)>();
        var zoneTexData = new List<ReadOnlyMemory<byte>>();
        foreach (var file in zoneTexFiles)
        {
            var data = File.ReadAllBytes(file);
            zoneTexData.Add(data);
            var uploads = ThawZoneTexFile.ParseVramUploads(data);
            var headerEntries = ThawZoneTexFile.ParseHeaderEntries(data);
            mergedUploads.AddRange(uploads);
            zoneSources.Add((data, uploads, headerEntries));

            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {Path.GetFileName(file)}: [green]{uploads.Count} VRAM uploads[/], [green]{headerEntries.Count} header entries[/]");
            }
        }

        AnsiConsole.MarkupLine(
            $"Merged [green]{mergedUploads.Count}[/] VRAM uploads");

        var checksumMap = ThawZoneTexFile.BuildChecksumMapFromHeaders(zoneTexData);
        var headerSourceEntryMapByTex0 = ThawZoneTexFile.BuildHeaderSourceEntryMapByTex0FromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));
        var headerEntryMap = ThawZoneTexFile.BuildHeaderEntryMapFromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));
        var sourceIndexMap = ThawZoneTexFile.BuildSourceIndexMapFromHeaderLists(
            zoneSources.Select(static source => (IReadOnlyList<ThawZoneTexFile.ZoneTexHeaderEntry>)source.Headers));
        var textureMap = new Dictionary<uint, Ps2Texture>();

        for (var sourceIndex = 0; sourceIndex < zoneSources.Count; sourceIndex++)
        {
            var decodedHeaders = ThawZoneTexFile.DecodeFromHeaderEntries(
                zoneSources[sourceIndex].FileData.Span,
                zoneSources[sourceIndex].Uploads,
                zoneSources[sourceIndex].Headers,
                gifQwordWordOrder);
            foreach (var texture in decodedHeaders)
                textureMap.TryAdd(texture.Checksum, texture);
        }

        // Collect TEX0 values from companion MDL files
        // Search directories containing any of the zone TEX files
        var textureStates = new HashSet<ThawZoneTexFile.MdlGsTextureState>();
        var searchedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in zoneTexFiles)
        {
            var dir = Path.GetDirectoryName(file);
            if (dir != null) searchedDirs.Add(dir);
            var parent = dir != null ? Path.GetDirectoryName(dir) : null;
            if (parent != null)
                foreach (var sibling in Directory.GetDirectories(parent))
                    searchedDirs.Add(sibling);
        }

        foreach (var searchDir in searchedDirs)
        {
            foreach (var mdlFile in Directory.GetFiles(searchDir, "*.mdl", SearchOption.AllDirectories))
            {
                try
                {
                    var mdlData = File.ReadAllBytes(mdlFile);
                    var mdlTextureStates = ThawZoneTexFile.ExtractTextureStatesFromMdl(mdlData);
                    if (mdlTextureStates.Count > 0)
                    {
                        foreach (var state in mdlTextureStates) textureStates.Add(state);
                        if (verbose)
                            AnsiConsole.MarkupLine(
                                $"  MDL {Path.GetFileName(mdlFile)}: [green]{mdlTextureStates.Count} GS texture states[/]");
                    }
                }
                catch
                {
                    // Skip unparseable MDL files
                }
            }
        }

        AnsiConsole.MarkupLine(
            $"Collected [green]{textureStates.Count}[/] unique GS texture states from companion MDL files");

        var unresolvedStates = textureStates
            .Where(state =>
            {
                if (headerSourceEntryMapByTex0.TryGetValue(state.Tex0, out var headerSourceEntry))
                    return !textureMap.ContainsKey(headerSourceEntry.Entry.Checksum);

                var tbp = (uint)(state.Tex0 & 0x3FFF);
                var cbp = (uint)((state.Tex0 >> 37) & 0x3FFF);
                var checksum = checksumMap.TryGetValue((tbp, cbp), out var headerChecksum)
                    ? headerChecksum
                    : (tbp << 16) | cbp;
                return checksum != 0 && !textureMap.ContainsKey(checksum);
            })
            .ToList();

        if (unresolvedStates.Count > 0)
        {
            var fallbackHeadersBySource = unresolvedStates
                .Select(state => headerSourceEntryMapByTex0.TryGetValue(state.Tex0, out var headerSourceEntry)
                    ? (ThawZoneTexFile.ZoneTexHeaderSourceEntry?)headerSourceEntry
                    : null)
                .Where(static headerSourceEntry => headerSourceEntry.HasValue)
                .Select(static headerSourceEntry => headerSourceEntry!.Value)
                .GroupBy(static headerSourceEntry => headerSourceEntry.SourceIndex);

            foreach (var group in fallbackHeadersBySource)
            {
                var headerEntries = group
                    .Select(static headerSourceEntry => headerSourceEntry.Entry)
                    .DistinctBy(static entry => entry.Checksum)
                    .ToList();
                var decodedTextures = ThawZoneTexFile.DecodeFromHeaderEntries(
                    zoneSources[group.Key].FileData.Span,
                    zoneSources[group.Key].Uploads,
                    headerEntries,
                    gifQwordWordOrder);
                foreach (var texture in decodedTextures)
                    textureMap.TryAdd(texture.Checksum, texture);
            }

            var unresolvedTex0 = unresolvedStates
                .Where(state => !headerSourceEntryMapByTex0.ContainsKey(state.Tex0))
                .Select(static state => state.Tex0)
                .Distinct()
                .ToList();

            if (unresolvedTex0.Count > 0)
            {
                foreach (var group in unresolvedTex0
                             .Where(tex0 =>
                             {
                                 var tbp = (uint)(tex0 & 0x3FFF);
                                 var cbp = (uint)((tex0 >> 37) & 0x3FFF);
                                 return sourceIndexMap.ContainsKey((tbp, cbp));
                             })
                             .GroupBy(tex0 =>
                             {
                                 var tbp = (uint)(tex0 & 0x3FFF);
                                 var cbp = (uint)((tex0 >> 37) & 0x3FFF);
                                 return sourceIndexMap[(tbp, cbp)];
                             }))
                {
                    var fallbackHeaders = group
                        .Select(tex0 =>
                        {
                            var tbp = (uint)(tex0 & 0x3FFF);
                            var cbp = (uint)((tex0 >> 37) & 0x3FFF);
                            return headerEntryMap.TryGetValue((tbp, cbp), out var headerEntry)
                                ? headerEntry
                                : default;
                        })
                        .Where(static entry => entry.Checksum != 0)
                        .ToList();
                    var fallbackTextures = fallbackHeaders.Count > 0
                        ? ThawZoneTexFile.DecodeFromHeaderEntries(zoneSources[group.Key].FileData.Span,
                            zoneSources[group.Key].Uploads, fallbackHeaders, gifQwordWordOrder)
                        : ThawZoneTexFile.DecodeFromTex0Values(zoneSources[group.Key].Uploads, group, checksumMap,
                            gifQwordWordOrder);
                    foreach (var texture in fallbackTextures)
                        textureMap.TryAdd(texture.Checksum, texture);
                }
            }
        }

        var textures = textureMap.Values.ToList();

        if (textures.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]No textures decoded from zone TEX VRAM map[/]");
            return new ZoneTexBatchResult(0, 0, zoneTexFiles.Count);
        }

        // Save as PNGs under "zone_tex" subdirectory
        var result = new Ps2TexResult(textures);
        var count = Ps2TexFile.SaveAllAsPng(result, output, "zone_tex");

        AnsiConsole.MarkupLine(
            $"Zone TEX: [green]{count} textures[/] decoded from time-aware VRAM upload stream");

        return new ZoneTexBatchResult(count, zoneTexFiles.Count, 0);
    }

    /// <summary>
    ///     Scan the same directory (and sibling directories) for companion MDL files
    ///     and extract their TEX0 register values for zone TEX decoding.
    /// </summary>
    private static HashSet<ulong> FindCompanionMdlTex0Values(string texFile, bool verbose)
    {
        var tex0Values = new HashSet<ulong>();
        var dir = Path.GetDirectoryName(texFile);
        if (dir == null) return tex0Values;

        // Scan same directory and parent directory for MDL files
        var searchDirs = new List<string> { dir };
        var parent = Path.GetDirectoryName(dir);
        if (parent != null)
        {
            // Also scan sibling directories (other extracted PAKs)
            foreach (var sibling in Directory.GetDirectories(parent))
                searchDirs.Add(sibling);
        }

        foreach (var searchDir in searchDirs)
        {
            foreach (var mdlFile in Directory.GetFiles(searchDir, "*.mdl", SearchOption.AllDirectories))
            {
                try
                {
                    var mdlData = File.ReadAllBytes(mdlFile);
                    var mdlTex0 = ThawZoneTexFile.ExtractTex0ValuesFromMdl(mdlData);
                    if (mdlTex0.Count > 0)
                    {
                        foreach (var v in mdlTex0) tex0Values.Add(v);
                        if (verbose)
                            AnsiConsole.MarkupLine(
                                $"  MDL {Path.GetFileName(mdlFile)}: [green]{mdlTex0.Count} TEX0 values[/]");
                    }
                }
                catch
                {
                    // Skip unparseable MDL files
                }
            }
        }

        return tex0Values;
    }

    /// <summary>
    ///     Detects PS2 texture files by extension (.tex, .img) or compound extension (.tex.ps2, .img.ps2).
    /// </summary>
    private static bool IsPs2TextureFile(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".tex", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".tex.ps2", StringComparison.OrdinalIgnoreCase)
               || name.EndsWith(".img.ps2", StringComparison.OrdinalIgnoreCase);
    }

    private record struct ZoneTexBatchResult(int Textures, int FilesConverted, int FilesFailed);
}
