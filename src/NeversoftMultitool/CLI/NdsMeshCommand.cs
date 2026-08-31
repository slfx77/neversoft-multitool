using System.CommandLine;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Nds;
using NeversoftMultitool.Core.Formats.Texture.Nds;
using Spectre.Console;

namespace NeversoftMultitool.CLI;

/// <summary>
///     Converts the Vicarious Visions DS carts' models to glTF.
///
///     A DS geometry file is a packed Nintendo GX display list behind an 84-byte
///     header, and the container names it <c>.\%08x.%08x.geometry.bin</c> from two
///     ids the ARM9 holds. So the command works over the container (a <c>.gob</c>
///     beside its <c>.gfc</c>, or a <c>.nds</c> cart it opens straight through)
///     rather than over a file list — and because those ids also name the model's
///     texture bank, the two halves are bound by spelling rather than by guesswork.
/// </summary>
public static class NdsMeshCommand
{
    public static Command Create()
    {
        var inputArgument = new Argument<string>("input")
        {
            Description = "A DS cart (.nds) or its GOB container (.gob, beside its .gfc)"
        };
        var outputOption = new Option<string?>("-o", "--output")
        {
            Description = "Output directory for the converted models"
        };
        var limitOption = new Option<int>("--limit")
        {
            Description = "Convert at most this many models (0 = all)",
            DefaultValueFactory = _ => 0
        };
        var verboseOption = new Option<bool>("-v", "--verbose") { Description = "List each model" };
        var animationsOption = new Option<bool>("--animations")
        {
            Description = "Bake each model's animation clips (Sk8land's indexed clip library) "
                          + "into skinned, animated glTF"
        };
        var levelsOption = new Option<bool>("--levels")
        {
            Description = "Composite each multi-piece model set into one level glTF "
                          + "(pieces are authored in world space and simply merge)"
        };

        var command = new Command("nds-mesh", "Convert Nintendo DS (Vicarious Visions) models to glTF");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(verboseOption);
        command.Options.Add(animationsOption);
        command.Options.Add(levelsOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(limitOption),
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(animationsOption),
                parseResult.GetValue(levelsOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, int limit, bool verbose,
        bool animations = false, bool levels = false,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(input))
        {
            AnsiConsole.MarkupLine($"[red]Input not found:[/] {Markup.Escape(input)}");
            return 1;
        }

        using var container = NdsContainer.Open(input);
        if (container == null)
        {
            AnsiConsole.MarkupLine(
                "[red]No GOB container found.[/] Pass a DS cart (.nds) or a .gob beside its .gfc.");
            return 1;
        }

        var output = outputDir ?? Path.Combine(
            Path.GetDirectoryName(Path.GetFullPath(input)) ?? ".",
            Path.GetFileNameWithoutExtension(input) + "_models");
        Directory.CreateDirectory(output);

        var catalog = NdsTextureLookup.Build(container);
        var clipSource = animations ? NdsClipSource.Build(container) : null;
        if (levels)
            return ExportLevels(container, catalog, output, verbose, cancellationToken);

        var naming = NdsModelNaming.For(container);
        var converted = 0;
        var empty = 0;
        var triangles = 0;
        var textured = 0;
        var animated = 0;
        var clipCount = 0;

        foreach (var entry in container.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit > 0 && converted >= limit)
                break;

            var model = TryBuild(container, entry, catalog, clipSource, naming);
            if (model == null)
                continue;
            if (model.Animations.Count > 0)
            {
                animated++;
                clipCount += model.Animations.Count;
            }
            if (model.TriangleCount == 0)
            {
                empty++;
                continue;
            }

            var result = ModelExportService.Export(model, new MeshExportRequest
            {
                OutputDirectory = output,
                OutputStem = model.Name,
                CancellationToken = cancellationToken
            });

            converted++;
            triangles += model.TriangleCount;
            if (model.Textures.Count > 0)
                textured++;
            if (verbose)
                Report(model, result.OutputPaths.Count > 0);
        }

        AnsiConsole.MarkupLine(
            $"Converted [green]{converted}[/] models ([green]{triangles}[/] triangles), "
            + $"[green]{textured}[/] with resolved textures"
            + (animated > 0 ? $", [green]{animated}[/] animated ({clipCount} clips)" : "")
            + (empty > 0 ? $", [yellow]{empty}[/] with no geometry" : ""));
        return converted > 0 ? 0 : 1;
    }

    /// <summary>
    ///     Composites every multi-piece model set into one document. See
    ///     <see cref="NdsLevelCompositor" /> for why merging is the whole job: the
    ///     pieces are authored in world space and share their set's texture bank.
    ///     <see cref="NdsModelSetBounds" /> then says whether the set is a LEVEL or a
    ///     many-part model, which the container spells identically, and the output is
    ///     named <c>level_</c> or <c>set_</c> accordingly.
    /// </summary>
    private static int ExportLevels(
        IArchiveFileSystem container, NdsTextureLookup catalog, string output,
        bool verbose, CancellationToken cancellationToken)
    {
        var naming = NdsModelNaming.For(container);
        var exported = 0;
        var worlds = 0;
        var entities = 0;
        var pieces = 0;
        var triangles = 0;
        var authored = 0;

        var sets = NdsLevelComposer.GroupSets(container);
        foreach (var (idA, members) in sets.OrderByDescending(s => s.Value.Count))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (members.Count < 2)
                continue;

            // The cart NAMES its sets, which settles level-or-model outright; the
            // size measurement is the answer for a bare .gob, which has no code.
            naming.Sets.TryGetValue(idA, out var setName);
            var isLevel = setName != null
                ? NdsSetNames.IsLevel(setName)
                : NdsModelSetBounds.IsWorldScale(ReadGeometry(container, members));
            var name = setName != null
                ? NdsSetNames.ToStem(NdsSetNames.DisplayName(setName))
                : $"{(isLevel ? "level" : "set")}_{idA:x8}";

            var composed = NdsLevelComposer.Compose(
                container, idA, name, catalog, naming, placeEntities: isLevel, cancellationToken);
            if (composed == null)
                continue;

            var result = ModelExportService.Export(composed.Document, new MeshExportRequest
            {
                OutputDirectory = output,
                OutputStem = name,
                CancellationToken = cancellationToken
            });
            exported++;
            if (isLevel)
                worlds++;
            pieces += composed.Pieces;
            authored += composed.NamedPieces;
            entities += composed.Entities;
            triangles += composed.Document.TriangleCount;
            if (verbose)
            {
                AnsiConsole.MarkupLine(
                    $"  {name} [grey]{composed.Pieces} pieces, {composed.Document.TriangleCount} tris, "
                    + $"{composed.LiftedDecals} decals lifted"
                    + (composed.Entities > 0 ? $", {composed.Entities} entities placed" : "") + "[/]"
                    + (result.OutputPaths.Count > 0 ? "" : " [red]export failed[/]"));
            }
        }

        if (entities > 0)
            AnsiConsole.MarkupLine($"Placed [green]{entities}[/] gameplay entities.");
        AnsiConsole.MarkupLine(
            $"Composited [green]{exported}[/] model sets — [green]{worlds}[/] world-scale "
            + $"([green]{pieces}[/] pieces, [green]{triangles}[/] triangles, "
            + $"[green]{authored}[/] named)");
        return 0;
    }

    /// <summary>The parsed geometry of a set's pieces, for the size fallback.</summary>
    private static List<NdsGeometryFile> ReadGeometry(
        IArchiveFileSystem container, List<(uint IdB, ArchiveEntry Entry)> members)
    {
        var parsed = new List<NdsGeometryFile>();
        foreach (var (_, entry) in members)
        {
            try
            {
                if (NdsGeometryFile.TryParseValidated(container.ReadEntry(entry), out var geometry))
                    parsed.Add(geometry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                // A piece that will not read contributes nothing to the verdict.
            }
        }

        return parsed;
    }

    /// <summary>
    ///     The export stem a cart's own names give an entry, or null to keep its
    ///     filename (a bare .gob carries no code, so it has no names).
    /// </summary>
    private static string? StemFor(ArchiveEntry entry, NdsModelNames? naming)
    {
        return naming != null
               && NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out var idB)
            ? naming.StemFor(idA, idB)
            : null;
    }

    internal static ModelDocument BuildDocument(
        string name,
        NdsGeometryFile geometry,
        IReadOnlyList<NdsGeometryGroup> groups,
        NdsTextureSource? textures = null)
    {
        var document = ModelDocument.CreateNative(
            name, ModelSourceKind.Generic, new NdsGeometryNativeSource(geometry, groups));
        NdsGeometryWriter.PopulateNdsGeometry(document, geometry, groups, textures);
        return document;
    }

    /// <summary>Reads one container entry and builds a model, or null if it is not geometry.</summary>
    private static ModelDocument? TryBuild(
        IArchiveFileSystem container, ArchiveEntry entry, NdsTextureLookup catalog,
        NdsClipSource? clipSource = null, NdsModelNames? naming = null)
    {
        byte[] data;
        try
        {
            data = container.ReadEntry(entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }

        if (!NdsGeometryFile.TryParseValidated(data, out var geometry))
            return null;

        var groups = NdsGxInterpreter.Run(data, geometry);
        var textures = catalog.For(entry, groups);
        var name = StemFor(entry, naming) ?? Path.GetFileNameWithoutExtension(entry.Name);

        var clips = clipSource?.ClipsFor(entry) ?? [];
        if (clips.Count > 0)
        {
            var document = ModelDocument.CreateNative(
                name, ModelSourceKind.Generic, new NdsGeometryNativeSource(geometry, groups));
            if (NdsAnimatedModelWriter.TryPopulate(document, data, geometry, clips, textures) > 0)
                return document;
        }

        return BuildDocument(name, geometry, groups, textures);
    }

    private static void Report(ModelDocument model, bool exported)
    {
        var source = (NdsGeometryNativeSource)model.NativeSource!;
        AnsiConsole.MarkupLine(
            $"  {Markup.Escape(model.Name)} [grey]{model.TriangleCount} tris, "
            + $"{source.Groups.Count} groups, joints {source.File.JointCount}, "
            + $"{model.Textures.Count} textures[/]"
            + (exported ? "" : " [red]export failed[/]"));
    }
}

/// <summary>
///     Composites multi-piece model sets into levels.
///
///     A DS level IS a model set: its overlay's manifest lists one idA with a run
///     of geometry idBs (Sk8land's downtown set carries 135 pieces), and the
///     pieces are authored in WORLD space — their header bounding boxes tile the
///     level's whole footprint rather than centring on the origin (measured:
///     2 of 135 pieces near origin, combined extent ~1,140x1,136 units). So a
///     level assembles by simply merging every geometry file that shares an idA,
///     the same way THAW worldzone sectors do; the pieces even share the set's
///     one texture bank by construction.
/// </summary>
internal static class NdsLevelCompositor
{
    /// <summary>Groups the container's named geometry entries by model-set idA.</summary>
    public static Dictionary<uint, List<(uint IdB, ArchiveEntry Entry)>> GroupSets(
        IArchiveFileSystem container)
    {
        var sets = new Dictionary<uint, List<(uint, ArchiveEntry)>>();
        foreach (var entry in container.Entries)
        {
            if (!NdsModelSet.TryParseGeometryName(
                    GobNames.TryResolve(entry.Crc), out var idA, out var idB))
            {
                continue;
            }

            if (!sets.TryGetValue(idA, out var list))
                sets[idA] = list = [];
            list.Add((idB, entry));
        }

        return sets;
    }
}

/// <summary>
///     Finds a model's animation clips by the loader's own naming: model
///     <c>.\&lt;idA&gt;.&lt;idB&gt;.geometry.bin</c> owns clips
///     <c>.\&lt;idA&gt;.&lt;idB&gt;.&lt;n&gt;.animation.bin</c>, contiguous from 0.
///     Only models whose name was recovered can reach their clips — the ids are
///     unrecoverable from content alone.
/// </summary>
internal sealed class NdsClipSource
{
    private readonly IArchiveFileSystem _container;
    private readonly Dictionary<uint, ArchiveEntry> _byKey;

    private NdsClipSource(IArchiveFileSystem container, Dictionary<uint, ArchiveEntry> byKey)
    {
        _container = container;
        _byKey = byKey;
    }

    public static NdsClipSource Build(IArchiveFileSystem container)
    {
        var byKey = new Dictionary<uint, ArchiveEntry>(container.Entries.Count);
        foreach (var entry in container.Entries)
            byKey[entry.Crc] = entry;
        return new NdsClipSource(container, byKey);
    }

    public List<(string Name, NdsAnimationFile Clip)> ClipsFor(ArchiveEntry geometryEntry)
    {
        var clips = new List<(string, NdsAnimationFile)>();
        if (!NdsModelSet.TryParseGeometryName(
                GobNames.TryResolve(geometryEntry.Crc), out var idA, out var idB))
        {
            return clips;
        }

        for (var n = 0; ; n++)
        {
            if (!_byKey.TryGetValue(NdsModelSet.ClipKey(idA, idB, n), out var entry))
                break;

            byte[] data;
            try
            {
                data = _container.ReadEntry(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            if (NdsAnimationFile.TryParse(data, out var clip))
                clips.Add(($"anim_{n}", clip));
        }

        if (clips.Count > 0)
            return clips;

        // Downhill Jam and Proving Ground give a model ONE clip under a single
        // opaque id that nothing in the model itself carries — those ids hash onto
        // no authored name and coincide with no geometry id. The cart's manifest
        // record states the link beside the geometry pair.
        var animationId = NdsModelNaming.For(_container).AnimationIdFor(idA, idB);
        if (animationId == 0)
            return clips;
        var stated = _container.FindByPath($"{animationId:x8}.animation.bin");
        if (stated == null)
            return clips;

        try
        {
            if (NdsAnimationFile.TryParse(_container.ReadEntry(stated), out var single))
                clips.Add(("anim_0", single));
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // A clip that will not read leaves the model static.
        }

        return clips;
    }
}

/// <summary>
///     Opens the GOB container a DS command needs, whether it was handed the GOB
///     itself or the cart around it. Shared by the texture and mesh commands
///     because both need BOTH halves of a split asset in hand at once.
/// </summary>
internal static class NdsContainer
{
    public static IArchiveFileSystem? Open(string input)
    {
        var direct = ArchiveFileSystem.TryOpen(input);
        if (direct == null)
            return null;
        if (direct.Type == ArchiveAssetType.Gob)
            return direct;

        foreach (var entry in direct.Entries)
        {
            if (!entry.Name.EndsWith(".gob", StringComparison.OrdinalIgnoreCase))
                continue;
            var nested = direct.TryOpenNested(entry);
            if (nested != null)
                return new OwnedArchive(nested, direct);
        }

        direct.Dispose();
        return null;
    }

    /// <summary>Keeps the parent cart alive for as long as the nested GOB is in use.</summary>
    private sealed class OwnedArchive(IArchiveFileSystem inner, IArchiveFileSystem owner)
        : IArchiveFileSystem
    {
        public string DisplayPath => inner.DisplayPath;
        public string ContainerPath => inner.ContainerPath;
        public ArchiveAssetType Type => inner.Type;
        public int NestingDepth => inner.NestingDepth;
        public IArchiveFileSystem? Parent => inner.Parent;
        public IReadOnlyList<ArchiveEntry> Entries => inner.Entries;

        public byte[] ReadEntry(ArchiveEntry entry) => inner.ReadEntry(entry);
        public ArchiveEntry? FindByPath(string relativePath) => inner.FindByPath(relativePath);
        public ArchiveEntry? FindByName(string basename) => inner.FindByName(basename);

        public IReadOnlyList<ArchiveEntry> FindAllByName(string basename)
            => inner.FindAllByName(basename);

        public IArchiveFileSystem? TryOpenNested(ArchiveEntry entry) => inner.TryOpenNested(entry);

        public void Dispose()
        {
            inner.Dispose();
            owner.Dispose();
        }
    }
}


/// <summary>
///     Places a level's gameplay entities — the S-K-A-T-E letters, the trick orbs,
///     the pedestrians and props — into its composited document.
///
///     A level's <c>.prp</c> names each entity by its model set's authored name and
///     gives it a world position; the set is a one-piece model whose two ids are
///     equal, so drawing it is one more geometry file transformed into place. The
///     rotation the record also carries is NOT applied — it is degrees, but the axis
///     is unestablished, and a wrong axis reads worse than none.
/// </summary>
internal static class NdsEntityPlacer
{
    public static int Place(
        ModelDocument document,
        IArchiveFileSystem container,
        NdsTextureLookup catalog,
        string levelSetName,
        NdsModelNames naming)
    {
        var dataName = NdsLevelEntities.DataFileFor(levelSetName);
        if (dataName == null)
            return 0;
        var dataEntry = container.FindByPath(dataName);
        if (dataEntry == null)
            return 0;

        byte[] data;
        try
        {
            data = container.ReadEntry(dataEntry);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return 0;
        }

        var placements = NdsLevelEntities.Parse(data, NdsModelSetIds(container));
        if (placements.Count == 0)
            return 0;

        var placed = 0;
        var cache = new Dictionary<string, (ArchiveEntry Entry, byte[] Data, NdsGeometryFile File)?>();
        foreach (var placement in placements)
        {
            if (!cache.TryGetValue(placement.ModelName, out var model))
                cache[placement.ModelName] = model = LoadEntity(container, placement.SetId);
            if (model == null)
                continue;

            var (entry, bytes, geometry) = model.Value;
            var groups = NdsGxInterpreter.Run(bytes, geometry);
            var before = document.Meshes.Count;
            NdsGeometryWriter.PopulateNdsGeometry(
                document, geometry, groups, catalog.For(entry, groups),
                namePrefix: $"entity_{placement.ModelName}_{placed:D3}_");

            // The writer emits in the file's own space; the placement moves it,
            // through the same Z-up to Y-up basis the writer uses for positions.
            var offset = new Vector3(placement.Position.X, placement.Position.Z, -placement.Position.Y);
            for (var m = before; m < document.Meshes.Count; m++)
            foreach (var primitive in document.Meshes[m].Primitives)
            {
                for (var v = 0; v < primitive.Vertices.Length; v++)
                {
                    primitive.Vertices[v] = primitive.Vertices[v] with
                    {
                        Position = primitive.Vertices[v].Position + offset
                    };
                }
            }

            placed++;
        }

        return placed;
    }

    /// <summary>Every model-set id the container holds.</summary>
    private static HashSet<uint> NdsModelSetIds(IArchiveFileSystem container)
    {
        var ids = new HashSet<uint>();
        foreach (var entry in container.Entries)
        {
            if (NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out _))
                ids.Add(idA);
        }

        return ids;
    }

    private static (ArchiveEntry, byte[], NdsGeometryFile)? LoadEntity(
        IArchiveFileSystem container, uint idA)
    {
        // A gameplay entity is its own one-piece set, keyed by the same id twice.
        var entry = container.FindByPath(
            NdsModelSet.GeometryName(idA, idA)[2..]);
        if (entry == null)
            return null;

        try
        {
            var bytes = container.ReadEntry(entry);
            return NdsGeometryFile.TryParseValidated(bytes, out var geometry)
                ? (entry, bytes, geometry)
                : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
