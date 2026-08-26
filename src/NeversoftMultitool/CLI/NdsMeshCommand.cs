using System.CommandLine;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using System.Globalization;
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

        var command = new Command("nds-mesh", "Convert Nintendo DS (Vicarious Visions) models to glTF");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(verboseOption);
        command.Options.Add(animationsOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(limitOption),
                parseResult.GetValue(verboseOption),
                parseResult.GetValue(animationsOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, int limit, bool verbose,
        bool animations = false, CancellationToken cancellationToken = default)
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

        var catalog = NdsTextureCatalog.Build(container);
        var clipSource = animations ? NdsClipSource.Build(container) : null;
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

            var model = TryBuild(container, entry, catalog, clipSource);
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
        IArchiveFileSystem container, ArchiveEntry entry, NdsTextureCatalog catalog,
        NdsClipSource? clipSource = null)
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
        var textures = catalog.ResolveFor(entry, groups);
        var name = Path.GetFileNameWithoutExtension(entry.Name);

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
///     The texture halves of a DS container: every parsed bank, and every texel blob
///     indexed by the id its filename encodes.
///
///     A model's bank is STATED rather than inferred whenever the model's name was
///     recovered — the two share the model set's first id (see
///     <see cref="NdsModelSet" />). For a model with no recovered name the bank falls
///     back to <see cref="NdsTextureBankResolver" />, which joins on the GX state
///     both sides declare and speaks only when exactly one bank is compatible.
/// </summary>
internal sealed class NdsTextureCatalog
{
    private readonly List<IReadOnlyList<NdsTextureEntry>> _banks = [];
    private readonly Dictionary<uint, IReadOnlyList<NdsTextureEntry>> _banksByKey = [];
    private readonly Dictionary<uint, ArchiveEntry> _texels = [];
    private readonly IArchiveFileSystem _container;

    private NdsTextureCatalog(IArchiveFileSystem container)
    {
        _container = container;
    }

    public static NdsTextureCatalog Build(IArchiveFileSystem container)
    {
        var catalog = new NdsTextureCatalog(container);
        foreach (var entry in container.Entries)
        {
            var name = entry.Name;
            if (name.EndsWith(".texture.bin", StringComparison.OrdinalIgnoreCase)
                && uint.TryParse(name.AsSpan(0, Math.Min(8, name.Length)),
                    NumberStyles.HexNumber, null, out var id))
            {
                catalog._texels[id] = entry;
            }
        }

        long? PixelLength(uint id) => catalog._texels.TryGetValue(id, out var e) ? e.Size : null;

        foreach (var entry in container.Entries)
        {
            byte[] data;
            try
            {
                data = container.ReadEntry(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            if (!NdsTextureBank.TryParseValidated(data, PixelLength, out var bank))
                continue;

            catalog._banks.Add(bank);
            catalog._banksByKey[entry.Crc] = bank;
        }

        return catalog;
    }

    /// <summary>
    ///     The bank for one model. Prefers the binding the loader spells — the model
    ///     set's own id — and falls back to the GX-state join when the model's name
    ///     was not recovered.
    /// </summary>
    public NdsTextureSource? ResolveFor(ArchiveEntry entry, IReadOnlyList<NdsGeometryGroup> groups)
    {
        if (NdsModelSet.TryParseGeometryName(GobNames.TryResolve(entry.Crc), out var idA, out _)
            && _banksByKey.TryGetValue(NdsModelSet.TextureBankKey(idA), out var stated))
        {
            return new NdsTextureSource(stated, ReadTexels);
        }

        var joined = NdsTextureBankResolver.Resolve(groups, _banks);
        return joined == null ? null : new NdsTextureSource(joined, ReadTexels);
    }

    private byte[]? ReadTexels(uint pixelId)
    {
        if (!_texels.TryGetValue(pixelId, out var entry))
            return null;
        try
        {
            return _container.ReadEntry(entry);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }
}
