using System.CommandLine;
using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using Spectre.Console;

namespace PsxAnalyzer.Commands;

/// <summary>
///     Census of DETACHABLE PROPS on PSX character models — objects the engine
///     can hide per-part at runtime, which we would default to hidden.
///
///     The runtime gate is <c>CSuper::mSubObjectDisplayMask</c> (M3D.cpp:1435,
///     verified in thps1_final.exe at 0x800849DC): bit <i>i</i> hides part
///     <i>i</i>, indexed by the same loop index that indexes <c>ppModels[]</c>,
///     i.e. the .psx object index. Nothing in the object table or mesh header
///     marks a part hideable — for Muska's boombox the mask is set by code
///     hardcoded to his ComboID and cleared only while <c>mAnim == 63</c>.
///
///     A part whose mesh carries no stitch vertices sews to nothing and is
///     therefore DETACHABLE, which looked like a way to recognise optional props
///     generically. The proposed discriminator was that boards and wheels hang
///     off the skeleton ROOT while an optional prop hangs off a body part.
///
///     RESULT (2026-08-04): the discriminator does not hold, so nothing here is
///     wired into the converter. Measured over this corpus the rule would hide
///     933 objects across 245 character files — in THPS1 final alone 99 parts
///     across 33 files, where exactly ONE part (Muska's boombox) is actually
///     hidden in game. The 99 are overwhelmingly boards and wheels: a skater's
///     board attaches to a BODY PART, not the root (hawk.psx obj 0 hawk_board
///     has parent 3), so the root walk never separates them. Inside muska.psx
///     the Boombox (obj 12, parent 10) and muska_board (obj 17, parent 0) fall
///     in the same bucket, so the rule cannot even tell those two apart. The
///     earlier "33/33 board objects, one extra hit" figure came from checking
///     the 11 skater files against the exe's per-character Stats table, which is
///     game-binary data the file does not carry.
///
///     Conclusion: detachability IS file-derivable; "hidden by default" is NOT.
///     The visibility state is pure runtime state, and the boombox's gate is
///     hardcoded to Muska's ComboID in thps1_final.exe only (absent from the
///     1999-4-9 prototype, which ships no boombox mesh, and from THPS2). This
///     command is kept as the evidence for that decision.
/// </summary>
public static class PropCensusCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "Directory to scan recursively for .psx files (e.g. Sample/Builds)"
        };
        var verboseOption = new Option<bool>("-v", "--verbose")
        {
            Description = "List every file with a detachable prop"
        };

        var command = new Command(
            "prop-census",
            "Tally detachable character props (unstitched parts hanging off a body part)");
        command.Arguments.Add(inputArgument);
        command.Options.Add(verboseOption);

        command.SetAction((parseResult, _) =>
        {
            Run(parseResult.GetValue(inputArgument)!, parseResult.GetValue(verboseOption));
            return Task.FromResult(0);
        });

        return command;
    }

    private static void Run(string input, bool verbose)
    {
        if (!Directory.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Not a directory:[/] {input}");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        table.AddColumn("build");
        table.AddColumn("character files");
        table.AddColumn("board groups");
        table.AddColumn("PROPS (would hide)");
        table.AddColumn("files with props");

        var details = new List<string>();
        foreach (var build in Directory.GetDirectories(input).OrderBy(static path => path))
        {
            var tally = ScanBuild(build, details, verbose);
            if (tally.Files == 0)
                continue;

            Console.WriteLine(
                $"SUMMARY {Path.GetFileName(build),-52} characters={tally.Files,4} " +
                $"boards={tally.Boards,5} props={tally.Props,5} " +
                $"filesWithProps={tally.FilesWithProps,4}");
            table.AddRow(
                Path.GetFileName(build),
                tally.Files.ToString(CultureInfo.InvariantCulture),
                tally.Boards.ToString(CultureInfo.InvariantCulture),
                tally.Props.ToString(CultureInfo.InvariantCulture),
                tally.FilesWithProps.ToString());
        }

        AnsiConsole.Write(table);
        foreach (var detail in details)
            AnsiConsole.WriteLine(detail);
    }

    private readonly record struct BuildTally(int Files, int Boards, int Props, int FilesWithProps);

    private static BuildTally ScanBuild(string build, List<string> details, bool verbose)
    {
        var files = 0;
        var boards = 0;
        var props = 0;
        var filesWithProps = 0;

        foreach (var path in Directory.EnumerateFiles(build, "*.psx", SearchOption.AllDirectories))
        {
            var file = TryParse(path);

            // The visibility resolver only builds character groups for a
            // COMBINED CHARACTER ASSEMBLY, so the census uses the same gate:
            // level banks and vehicle files carry hierarchies too, and counting
            // their unstitched objects inflated this by two orders of magnitude.
            if (file == null || file.Objects.Count == 0 || !IsCharacterAssembly(file))
                continue;

            files++;
            var detachable = Enumerable.Range(0, file.Objects.Count)
                .Where(objectIndex => IsUnstitched(file, objectIndex))
                .ToLookup(objectIndex => ReachesRootThroughUnstitched(file, objectIndex));

            boards += detachable[true].Count();
            var fileProps = detachable[false]
                .Select(objectIndex => $"obj {objectIndex} ({MeshName(file, objectIndex)}) " +
                                       $"parent {file.Objects[objectIndex].ParentIndex}")
                .ToList();
            props += fileProps.Count;
            if (fileProps.Count == 0)
                continue;

            filesWithProps++;
            if (verbose)
                details.Add($"{Path.GetFileName(path)}: {string.Join(", ", fileProps)}");
        }

        return new BuildTally(files, boards, props, filesWithProps);
    }

    private static PsxMeshFile? TryParse(string path)
    {
        try
        {
            return PsxMeshFile.Parse(File.ReadAllBytes(path));
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Mirrors PsxMeshSemantics.UsesCombinedPsxCharacterAssembly (internal).</summary>
    private static bool IsCharacterAssembly(PsxMeshFile file)
    {
        return (file.HasHierarchy && file.IsSuperModel)
               || (file.HasStitchedReferences && file.Objects.Count == file.Meshes.Count);
    }

    /// <summary>
    ///     No stitch vertices of either kind: the part sews to no neighbour, so
    ///     the engine can remove it without tearing anything.
    /// </summary>
    private static bool IsUnstitched(PsxMeshFile file, int objectIndex)
    {
        var meshIndex = file.Objects[objectIndex].MeshIndex;
        if (meshIndex >= file.Meshes.Count)
            return false;

        return !file.Meshes[meshIndex].Vertices.Any(static vertex =>
            vertex.Type is PsxMeshSemanticsMirror.StitchSourceType
                or PsxMeshSemanticsMirror.StitchedReferenceType);
    }

    /// <summary>
    ///     Board/wheel groups hang off the skeleton ROOT through a chain of
    ///     unstitched objects; an optional prop hangs off a stitched body part
    ///     (Muska's boombox has parent 10, a forearm).
    /// </summary>
    private static bool ReachesRootThroughUnstitched(PsxMeshFile file, int objectIndex)
    {
        var current = objectIndex;
        for (var guard = 0; guard <= file.Objects.Count; guard++)
        {
            var parent = file.Objects[current].ParentIndex;
            if (parent < 0)
                return true;
            if (parent >= file.Objects.Count || !IsUnstitched(file, parent))
                return false;
            current = parent;
        }

        return false;
    }

    private static string MeshName(PsxMeshFile file, int objectIndex)
    {
        var meshIndex = file.Objects[objectIndex].MeshIndex;
        if (meshIndex >= file.MeshNameHashes.Length)
            return "?";
        var hash = file.MeshNameHashes[meshIndex];
        return NeversoftMultitool.Core.QbKey.QbKey.TryResolve(hash) ?? $"0x{hash:X8}";
    }

    /// <summary>Local mirror of the two stitch type ids (PsxMeshSemantics is internal).</summary>
    private static class PsxMeshSemanticsMirror
    {
        internal const ushort StitchSourceType = 0x0001;
        internal const ushort StitchedReferenceType = 0x0002;
    }
}
