using System.CommandLine;
using System.Diagnostics;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.DiscImage;
using NeversoftMultitool.Core.Formats.N64;
using NeversoftMultitool.Core.Formats.Nds;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class ArchiveCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description =
                "Path to archive file (WAD, PKR, PRE, PRX, PRD/PRF localized PRE, DDX, BON, PAK, ZIP, CUT) " +
                "or disc image (ISO, CUE, GDI, IMG+CCD, BIN)"
        };
        var outputOption = new Option<string>("-o", "--output")
        {
            Description = "Output directory for extracted files",
            DefaultValueFactory = _ => "TestOutput"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Enable verbose output"
        };

        var command = new Command("archive", "Extract files from WAD/PKR/PRE/PRX/PRD/PRF/DDX/BON/PAK/ZIP/CUT archives");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var input = parseResult.GetValue(inputArgument)!;
            var output = parseResult.GetValue(outputOption)!;
            var verbose = parseResult.GetValue(verboseOption);

            if (!File.Exists(input))
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] File not found: {Markup.Escape(input)}");
                return Task.FromResult(1);
            }

            var ext = RecursiveUnpacker.GetArchiveExtension(input);
            Directory.CreateDirectory(output);

            var stopwatch = Stopwatch.StartNew();
            var filesExtracted = 0;

            try
            {
                switch (ext)
                {
                    case ".wad":
                        AnsiConsole.MarkupLine("[blue]WAD[/] archive detected");
                        var wadEntries = WadArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{wadEntries.Count}[/] files");
                        WadArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(wadEntries[current - 1].Name));
                            }
                        }, cancellationToken);
                        break;

                    case ".pkr":
                        AnsiConsole.MarkupLine("[blue]PKR3[/] archive detected");
                        var pkrEntries = PkrArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{pkrEntries.Count}[/] files");
                        PkrArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(pkrEntries[current - 1].FullName));
                            }
                        }, cancellationToken);
                        break;

                    case ".ddx":
                        AnsiConsole.MarkupLine("[blue]DDX[/] texture archive detected");
                        var ddxEntries = DdxArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{ddxEntries.Count}[/] textures");
                        DdxArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(ddxEntries[current - 1].Name));
                            }
                        }, cancellationToken);
                        break;

                    case ".bon":
                        AnsiConsole.MarkupLine("[blue]BON[/] model archive detected");
                        var bonEntries = BonArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{bonEntries.Count}[/] textures");
                        BonArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(bonEntries[current - 1].Name));
                            }
                        }, cancellationToken);
                        break;

                    case ".pre" when CompressedPreArchive.IsCompressedPre(input):
                    case ".prd" when CompressedPreArchive.IsCompressedPre(input):
                    case ".prf" when CompressedPreArchive.IsCompressedPre(input):
                    case ".prg" when CompressedPreArchive.IsCompressedPre(input):
                    case ".prx":
                        AnsiConsole.MarkupLine("[blue]PRE v3[/] archive detected (LZSS compressed)");
                        var compressedPreEntries = CompressedPreArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{compressedPreEntries.Count}[/] files");
                        CompressedPreArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(compressedPreEntries[current - 1].FullName));
                            }
                        }, cancellationToken);
                        break;

                    case ".pre":
                    case ".prd":
                    case ".prf":
                    case ".prg":
                        AnsiConsole.MarkupLine("[blue]PRE[/] archive detected");
                        var preEntries = PreArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{preEntries.Count}[/] files");
                        PreArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(preEntries[current - 1].Name));
                            }
                        }, cancellationToken);
                        break;

                    case ".pak" when PakArchive.IsPakArchive(input):
                    case ".apk" when PakArchive.IsPakArchive(input):
                        AnsiConsole.MarkupLine("[blue]PAK[/] archive detected");
                        var pakEntries = PakArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{pakEntries.Count}[/] files");
                        PakArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(pakEntries[current - 1].FullName));
                            }
                        }, cancellationToken);
                        break;

                    case ".pak":
                    case ".apk":
                        AnsiConsole.MarkupLine(
                            "[red]PAK raw data file[/] (no entry table, not an extractable archive)");
                        return Task.FromResult(1);

                    case ".zip" when QZipArchive.IsZip(input):
                        AnsiConsole.MarkupLine("[blue]QTex ZIP[/] archive detected");
                        var zipEntries = QZipArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{zipEntries.Count}[/] files");
                        QZipArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(zipEntries[current - 1].FullName));
                            }
                        }, cancellationToken);
                        break;

                    case ".zip":
                        AnsiConsole.MarkupLine("[red]Not a PKZip file[/] (no local file header magic)");
                        return Task.FromResult(1);

                    case ".cut" when CutArchive.IsCut(input):
                        AnsiConsole.MarkupLine("[blue]CUT[/] cutscene container detected");
                        var cutEntries = CutArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{cutEntries.Count}[/] files");
                        CutArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                            {
                                AnsiConsole.MarkupLine(
                                    $"  [[{current}/{total}]] " +
                                    Markup.Escape(cutEntries[current - 1].FullName));
                            }
                        }, cancellationToken);
                        break;

                    case ".cut":
                        AnsiConsole.MarkupLine("[red]Not a cutscene file library[/] (bad header or layout)");
                        return Task.FromResult(1);

                    case ".iso" or ".cue" or ".gdi" or ".img" or ".bin"
                        when DiscImageArchive.IsDiscImage(input):
                        filesExtracted = ExtractDiscImage(input, output, verbose, cancellationToken);
                        break;

                    case ".iso" or ".cue" or ".gdi" or ".img" or ".bin":
                        AnsiConsole.MarkupLine(
                            "[red]Not a recognized disc image[/] (no ISO9660/XDVDFS/GCM filesystem found)");
                        return Task.FromResult(1);

                    case ".z64" when N64RomArchive.IsN64Rom(input):
                        AnsiConsole.MarkupLine("[blue]N64 ROM[/] detected (ERZ sub-file packing)");
                        var romEntries = N64RomArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{romEntries.Count}[/] entries");
                        N64RomArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]]");
                        }, cancellationToken);
                        break;

                    case ".z64":
                        var classification = N64RomArchive.ClassifyRom(input) ?? "Not an N64 ROM";
                        AnsiConsole.MarkupLine(
                            $"[red]{Markup.Escape(classification)}[/]");
                        return Task.FromResult(1);

                    case ".nds" when NdsRomArchive.IsNdsRom(input):
                        AnsiConsole.MarkupLine("[blue]NDS ROM[/] detected (Nitro filesystem)");
                        var ndsEntries = NdsRomArchive.GetFileList(input);
                        AnsiConsole.MarkupLine($"Found [green]{ndsEntries.Count}[/] entries");
                        NdsRomArchive.ExtractFiles(input, output, (current, total) =>
                        {
                            filesExtracted = current;
                            if (verbose)
                                AnsiConsole.MarkupLine($"  [[{current}/{total}]]");
                        }, cancellationToken);
                        break;

                    case ".nds":
                        AnsiConsole.MarkupLine("[red]Not a Nintendo DS ROM[/] (header/logo CRC or FNT/FAT check failed)");
                        return Task.FromResult(1);

                    default:
                        AnsiConsole.MarkupLine(
                            $"[red]Unsupported archive format:[/] {Markup.Escape(ext)}");
                        return Task.FromResult(1);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AnsiConsole.MarkupLine(
                    $"[red]Error:[/] {Markup.Escape(ex.Message)}");
                return Task.FromResult(1);
            }

            stopwatch.Stop();
            AnsiConsole.MarkupLine(
                $"Extracted [green]{filesExtracted}[/] files in {stopwatch.Elapsed.TotalSeconds:F2}s");

            return Task.FromResult(0);
        });

        return command;
    }

    private static int ExtractDiscImage(string input, string output, bool verbose, CancellationToken cancellationToken)
    {
        AnsiConsole.MarkupLine("[blue]Disc image[/] detected");
        var entries = DiscImageArchive.GetFileList(input);
        AnsiConsole.MarkupLine($"Found [green]{entries.Count}[/] files");
        var filesExtracted = 0;
        DiscImageArchive.ExtractFiles(input, output, (current, total) =>
        {
            filesExtracted = current;
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  [[{current}/{total}]] " +
                    Markup.Escape(entries[current - 1].FullName));
            }
        }, cancellationToken);
        return filesExtracted;
    }
}
