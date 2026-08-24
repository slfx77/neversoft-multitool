using System.CommandLine;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
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
///     runtime ids — so there is nothing to match on but structure, and the command
///     works over the container (a <c>.gob</c> beside its <c>.gfc</c>, or a
///     <c>.nds</c> cart it opens straight through) rather than over a file list.
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

        var command = new Command("nds-mesh", "Convert Nintendo DS (Vicarious Visions) models to glTF");
        command.Arguments.Add(inputArgument);
        command.Options.Add(outputOption);
        command.Options.Add(limitOption);
        command.Options.Add(verboseOption);
        command.SetAction((parseResult, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Execute(
                parseResult.GetValue(inputArgument)!,
                parseResult.GetValue(outputOption),
                parseResult.GetValue(limitOption),
                parseResult.GetValue(verboseOption),
                cancellationToken));
        });
        return command;
    }

    internal static int Execute(
        string input, string? outputDir, int limit, bool verbose,
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

        var catalog = NdsTextureCatalog.Build(container);
        var converted = 0;
        var empty = 0;
        var triangles = 0;
        var textured = 0;

        foreach (var entry in container.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (limit > 0 && converted >= limit)
                break;

            var model = TryBuild(container, entry, catalog);
            if (model == null)
                continue;
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
        IArchiveFileSystem container, ArchiveEntry entry, NdsTextureCatalog catalog)
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
        return BuildDocument(
            Path.GetFileNameWithoutExtension(entry.Name),
            geometry,
            groups,
            catalog.ResolveFor(groups));
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
///     A model does not name its bank — the id the loader composes the name from is
///     computed at runtime and stored nowhere — so the bank is identified by joining
///     on the GX state both sides declare. See <see cref="NdsTextureBankResolver" />.
/// </summary>
internal sealed class NdsTextureCatalog
{
    private readonly List<IReadOnlyList<NdsTextureEntry>> _banks = [];
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

            if (NdsTextureBank.TryParseValidated(data, PixelLength, out var bank))
                catalog._banks.Add(bank);
        }

        return catalog;
    }

    public NdsTextureSource? ResolveFor(IReadOnlyList<NdsGeometryGroup> groups)
    {
        var bank = NdsTextureBankResolver.Resolve(groups, _banks);
        return bank == null ? null : new NdsTextureSource(bank, ReadTexels);
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
