using System.Numerics;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Gob;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>What one composited model set turned out to hold.</summary>
public sealed record NdsCompositeResult(
    ModelDocument Document, int Pieces, int NamedPieces, int LiftedDecals, int Entities);

/// <summary>
///     Builds one DS model set into a single document — the whole job for a level,
///     because the pieces are authored in WORLD space and share the set's texture
///     bank, so compositing is merging rather than placing.
///
///     Three things are layered on top of the merge:
///     the cart's authored piece names, the cross-piece decal lift (a level ships
///     posters and shadows as separate pieces lying exactly on another piece's
///     plane, which the hardware resolves by draw order and glTF cannot), and the
///     gameplay entities the level's own <c>.prp</c> places.
///
///     Shared by <c>nds-mesh --levels</c> and the Levels tab so both produce the
///     same document; before this the compositing lived in the command alone.
/// </summary>
public static class NdsLevelComposer
{
    /// <summary>idA to the geometry entries sharing it, from the container's names.</summary>
    public static Dictionary<uint, List<(uint IdB, ArchiveEntry Entry)>> GroupSets(
        IArchiveFileSystem container)
    {
        ArgumentNullException.ThrowIfNull(container);
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

    /// <summary>
    ///     Composites the set keyed by <paramref name="idA" />, or null when fewer
    ///     than two of its pieces carry geometry.
    /// </summary>
    /// <param name="placeEntities">
    ///     Place the level's gameplay entities. Only meaningful for a level — a
    ///     many-part MODEL has no <c>.prp</c> — and off by default so a caller
    ///     compositing a character does not pay for the lookup.
    /// </param>
    public static NdsCompositeResult? Compose(
        IArchiveFileSystem container,
        uint idA,
        string outputStem,
        NdsTextureLookup textures,
        NdsModelNames naming,
        bool placeEntities,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(container);
        ArgumentNullException.ThrowIfNull(textures);
        ArgumentNullException.ThrowIfNull(naming);

        if (!GroupSets(container).TryGetValue(idA, out var members))
            return null;

        var parsed = new List<(uint IdB, ArchiveEntry Entry, byte[] Data, NdsGeometryFile File)>();
        foreach (var (idB, entry) in members.OrderBy(m => m.IdB))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] data;
            try
            {
                data = container.ReadEntry(entry);
            }
            catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            if (NdsGeometryFile.TryParseValidated(data, out var geometry))
                parsed.Add((idB, entry, data, geometry));
        }

        var document = new ModelDocument
        {
            Name = outputStem,
            SourceKind = ModelSourceKind.NdsLevel
        };
        var pieceOf = new Dictionary<ModelPrimitive, int>();
        var added = 0;
        var named = 0;
        foreach (var (idB, entry, data, geometry) in parsed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = document.Meshes.Count;
            var groups = NdsGxInterpreter.Run(data, geometry);
            var piece = naming.PieceName(idA, idB);
            if (piece != null)
                named++;
            NdsGeometryWriter.PopulateNdsGeometry(
                document, geometry, groups, textures.For(entry, groups),
                namePrefix: $"{piece ?? $"{idB:x8}"}_");
            for (var m = before; m < document.Meshes.Count; m++)
            foreach (var primitive in document.Meshes[m].Primitives)
                pieceOf[primitive] = added;
            added++;
        }

        if (added < 2 || document.TriangleCount == 0)
            return null;

        var lifted = NdsLevelOverlayResolver.Apply(document, pieceOf);
        var entities = placeEntities
            ? NdsEntityPlacer.Place(document, container, textures, naming, idA, cancellationToken)
            : 0;

        return new NdsCompositeResult(document, added, named, lifted, entities);
    }
}

/// <summary>
///     Places a level's gameplay entities into a composited document — the
///     S-K-A-T-E letters, the trick orbs, the pedestrians and props its
///     <c>.prp</c> names.
///
///     An entity is its own one-piece model set, so drawing it is one more geometry
///     file moved into place. The rotation the record also carries is NOT applied:
///     the values are whole degrees, but which axis they turn about is
///     unestablished, and a wrong axis reads worse than none.
/// </summary>
internal static class NdsEntityPlacer
{
    public static int Place(
        ModelDocument document,
        IArchiveFileSystem container,
        NdsTextureLookup textures,
        NdsModelNames naming,
        uint levelIdA,
        CancellationToken cancellationToken)
    {
        if (!naming.Sets.TryGetValue(levelIdA, out var levelName))
            return 0;
        var dataName = NdsLevelEntities.DataFileFor(levelName);
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

        var placements = NdsLevelEntities.Parse(data, naming.Sets.Keys.ToHashSet());
        if (placements.Count == 0)
            return 0;

        var placed = 0;
        var cache = new Dictionary<uint, (ArchiveEntry Entry, byte[] Data, NdsGeometryFile File)?>();
        foreach (var placement in placements)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!cache.TryGetValue(placement.SetId, out var model))
                cache[placement.SetId] = model = Load(container, placement.SetId);
            if (model == null)
                continue;

            var (entry, bytes, geometry) = model.Value;
            var groups = NdsGxInterpreter.Run(bytes, geometry);
            var before = document.Meshes.Count;
            NdsGeometryWriter.PopulateNdsGeometry(
                document, geometry, groups, textures.For(entry, groups),
                namePrefix: $"entity_{placement.ModelName}_{placed:D3}_");

            // The writer emits in the file's own space; the placement moves it,
            // through the same Z-up to Y-up basis the writer uses for positions.
            var offset = new Vector3(
                placement.Position.X, placement.Position.Z, -placement.Position.Y);
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

    private static (ArchiveEntry, byte[], NdsGeometryFile)? Load(
        IArchiveFileSystem container, uint idA)
    {
        // A gameplay entity is its own one-piece set, keyed by the same id twice.
        var entry = container.FindByPath(NdsModelSet.GeometryName(idA, idA)[2..]);
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
