using System.CommandLine;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

public static class PsxAnimDumpCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Path to a PSX character file"
        };
        var bytesOption = new Option<int>("--bytes")
        {
            Description = "Hex-dump window size after the mesh boundary",
            DefaultValueFactory = _ => 256
        };
        var animOption = new Option<int>("--anim")
        {
            Description = "Animation index to fully decompress in layer 4",
            DefaultValueFactory = _ => 0
        };
        var boneOption = new Option<int>("--bone")
        {
            Description = "Bone index within the animation to print frame-by-frame",
            DefaultValueFactory = _ => 0
        };
        var rankBoneOption = new Option<int?>("--rank-bone")
        {
            Description =
                "Diagnostic: decode all animation slots and rank them by this bone's translation span"
        };
        var rankTopOption = new Option<int>("--rank-top")
        {
            Description = "Number of ranked animation slots to print with --rank-bone",
            DefaultValueFactory = _ => 12
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "Print every layer in full"
        };

        var command = new Command("psxanim",
            "Probe a PS1 character .psx file for animation data (research diagnostic)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(bytesOption);
        command.Options.Add(animOption);
        command.Options.Add(boneOption);
        command.Options.Add(rankBoneOption);
        command.Options.Add(rankTopOption);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, cancellationToken) =>
        {
            var input = parseResult.GetValue(inputArgument)!;
            var bytes = parseResult.GetValue(bytesOption);
            var anim = parseResult.GetValue(animOption);
            var bone = parseResult.GetValue(boneOption);
            var rankBone = parseResult.GetValue(rankBoneOption);
            var rankTop = parseResult.GetValue(rankTopOption);
            var verbose = parseResult.GetValue(verboseOption);
            return Task.FromResult(Execute(
                input,
                bytes,
                anim,
                bone,
                rankBone,
                rankTop,
                verbose,
                cancellationToken));
        });

        return command;
    }

    internal static int Execute(
        string input,
        int hexBytes,
        int animIndex,
        int boneIndex,
        int? rankBoneIndex,
        int rankTop,
        bool verbose,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] File not found: {Markup.Escape(input)}");
            return 1;
        }

        var data = File.ReadAllBytes(input);
        cancellationToken.ThrowIfCancellationRequested();
        var fileName = Path.GetFileName(input);

        AnsiConsole.MarkupLine(
            $"[bold cyan]File:[/] {Markup.Escape(fileName)} ({data.Length:N0} bytes)");

        // ─── Mesh layer ────────────────────────────────────────────────
        var meshFile = PsxMeshFile.Parse(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (meshFile == null)
        {
            AnsiConsole.MarkupLine("[red]No mesh data — cannot locate post-mesh region.[/]");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[bold]Mesh layer:[/] version=0x{meshFile.Version:X2} hierarchy={meshFile.HasHierarchy} " +
            $"revision={meshFile.FormatRevision} meshes={meshFile.Meshes.Count} objects={meshFile.Objects.Count}");

        var parsedAnimFile = PsxAnimFile.Parse(data, meshFile.Objects.Count);
        cancellationToken.ThrowIfCancellationRequested();
        if (parsedAnimFile != null)
        {
            AnsiConsole.MarkupLine(
                $"[bold]Anim layer:[/] layout={parsedAnimFile.Layout} " +
                $"revision={parsedAnimFile.FormatRevision} runtime={parsedAnimFile.MinimumRuntimeRevision} " +
                $"chunk=0x{parsedAnimFile.ChunkTag:X2} entries={parsedAnimFile.Entries.Count}/{parsedAnimFile.NumStreamsDeclared}");
        }

        if (rankBoneIndex is { } rankedBone)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = PsxAnimDumpDecoder.DumpRankedBoneMotion(
                parsedAnimFile, meshFile.Objects.Count, rankedBone, rankTop);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }

        var boundary = PsxMeshFile.GetMeshBlockEnd(data);
        cancellationToken.ThrowIfCancellationRequested();
        if (boundary <= 0 || boundary >= data.Length)
        {
            AnsiConsole.MarkupLine($"[red]Boundary detection failed (= {boundary}).[/]");
            return 1;
        }

        var trailing = data.Length - boundary;
        AnsiConsole.MarkupLine(
            $"[bold]Mesh-block end:[/] 0x{boundary:X} ({boundary:N0}) — trailing {trailing:N0} bytes after meshes");

        if (trailing < 16)
        {
            AnsiConsole.MarkupLine("[yellow]Less than 16 trailing bytes — likely no anim block in this file.[/]");
            return 0;
        }

        // ─── Layer 1: hex dump + u32 interpretation ─────────────────────
        cancellationToken.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine("\n[bold underline]Layer 1[/] [grey]— hex dump after boundary[/]");
        PsxAnimDumpWalker.DumpHex(data, boundary, (int)Math.Min(hexBytes, trailing));
        PsxAnimDumpWalker.DumpFirstU32s(data, boundary, (int)Math.Min(16, trailing / 4));
        cancellationToken.ThrowIfCancellationRequested();

        // ─── Layer 2: speculative anim-packet walk ──────────────────────
        AnsiConsole.MarkupLine("\n[bold underline]Layer 2[/] [grey]— anim packet walk (PreProcessAnimPacket)[/]");
        var afterAnimPacket = PsxAnimDumpWalker.TryWalkAnimPacket(data, boundary, meshFile.Meshes.Count, verbose);
        cancellationToken.ThrowIfCancellationRequested();

        var hierarchyStart = afterAnimPacket;
        if (PsxMeshFile.TryGetAnimChunkTag(data, out var animChunkTag, out var chunkDataOffset))
        {
            hierarchyStart = chunkDataOffset;
            AnsiConsole.MarkupLine(
                $"  [grey]Tagged anim chunk found: tag=0x{animChunkTag:X} data=0x{chunkDataOffset:X}[/]");
        }

        // ─── Layer 3: speculative hierarchy walk ────────────────────────
        AnsiConsole.MarkupLine("\n[bold underline]Layer 3[/] [grey]— per-bone hierarchy walk[/]");
        cancellationToken.ThrowIfCancellationRequested();
        var psh = TryLoadPshCompanion(input);
        cancellationToken.ThrowIfCancellationRequested();
        var hierResult = PsxAnimDumpWalker.TryWalkHierarchy(data, hierarchyStart, psh, verbose);
        cancellationToken.ThrowIfCancellationRequested();

        // ─── Layer 4: decompress one whole animation (all bones, 6 channels each) ───
        if (hierResult is not null)
        {
            AnsiConsole.MarkupLine(
                $"\n[bold underline]Layer 4[/] [grey]— decompress animation {animIndex} (all bones)[/]");
            PsxAnimDumpDecoder.DumpAnimationSlot(data, hierResult, animIndex, boneIndex, meshFile.Objects.Count,
                verbose);
        }
        else
        {
            AnsiConsole.MarkupLine("\n[yellow]Layer 4 skipped: hierarchy not located.[/]");
        }

        cancellationToken.ThrowIfCancellationRequested();
        AnsiConsole.MarkupLine("\n[grey]Done. Iterate the heuristic if any layer looks wrong.[/]");
        cancellationToken.ThrowIfCancellationRequested();
        return 0;
    }

    // ─── Helpers ────────────────────────────────────────────────────────

    private static PshFile? TryLoadPshCompanion(string psxPath)
    {
        var stem = Path.Combine(
            Path.GetDirectoryName(psxPath) ?? "",
            Path.GetFileNameWithoutExtension(psxPath) + ".psh");
        return File.Exists(stem) ? PshFile.Parse(stem) : null;
    }
}
