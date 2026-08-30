using System.Collections.Concurrent;
using NeversoftMultitool.Core.Formats.ArchiveFs;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Names a DS model the way the studio did — <c>skate_s</c>, <c>videotape</c>,
///     <c>Level_Alcatraz_Visual__Box03</c> — instead of the two hex ids the loader
///     composes its filename from.
///
///     Both halves need the CART, not just the container: the set names come from
///     re-hashing strings in ARM9 and the overlays (<see cref="NdsSetNames" />) and
///     the per-piece names from the manifest tables there
///     (<see cref="NdsModelSetManifest" />). A bare extracted <c>.gob</c> carries no
///     code, so every name is optional and the caller keeps the filename.
///
///     Reading those tables costs a pass over a couple of megabytes of code, and a
///     scanner asks for one name per row across thousands of rows, so the result is
///     cached per container. The cache holds names, never archive handles, so it
///     cannot keep a file open.
/// </summary>
public static class NdsModelNaming
{
    private static readonly ConcurrentDictionary<string, NdsModelNames> Cache = new();

    /// <summary>
    ///     The names for one container, harvested once per distinct display path.
    ///     Returns an empty map — not null — for a container with no cart behind it.
    /// </summary>
    public static NdsModelNames For(IArchiveFileSystem container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return Cache.GetOrAdd(container.DisplayPath, _ => Build(container));
    }

    private static NdsModelNames Build(IArchiveFileSystem container)
    {
        var cart = container.Parent;
        if (cart == null)
            return NdsModelNames.Empty;

        try
        {
            var sets = NdsCartManifests.ReadSetNames(cart, container);
            var manifests = NdsCartManifests.Read(cart, container);
            var pieces = new Dictionary<(uint, uint), string>();
            foreach (var (idA, manifest) in manifests)
            foreach (var piece in manifest.Pieces)
            {
                if (piece.Name != null)
                    pieces[(idA, piece.IdB)] = piece.Name;
            }

            // The clip binding is read from the code directly rather than from the
            // manifests, because a manifest is only accepted for a set with several
            // geometry files — and most animated models are one-piece sets, so the
            // manifest route reached barely a seventh of them.
            return new NdsModelNames(
                sets, pieces, NdsCartManifests.ReadAnimationBindings(cart, container));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException
                                   or EndOfStreamException or NotSupportedException)
        {
            return NdsModelNames.Empty;
        }
    }
}

/// <summary>The authored names for one container's model sets and their pieces.</summary>
public sealed class NdsModelNames(
    IReadOnlyDictionary<uint, string> sets,
    IReadOnlyDictionary<(uint IdA, uint IdB), string> pieces,
    IReadOnlyDictionary<(uint IdA, uint IdB), uint> clips)
{
    public static NdsModelNames Empty { get; } = new(
        new Dictionary<uint, string>(),
        new Dictionary<(uint, uint), string>(),
        new Dictionary<(uint, uint), uint>());

    /// <summary>Set id to authored name.</summary>
    public IReadOnlyDictionary<uint, string> Sets { get; } = sets;

    /// <summary>
    ///     The clip a piece owns, in the carts that spell animation with one opaque
    ///     id per model rather than an indexed library. The manifest record carries
    ///     it beside the geometry pair, which is the only place the link exists:
    ///     Downhill Jam's and Proving Ground's animation ids are a population of
    ///     their own, hashing onto no name and matching no geometry id.
    /// </summary>
    public uint AnimationIdFor(uint idA, uint idB) =>
        clips.TryGetValue((idA, idB), out var id) ? id : 0;

    /// <summary>The artist's own name for one piece of a set, when the cart carries it.</summary>
    public string? PieceName(uint idA, uint idB) =>
        pieces.TryGetValue((idA, idB), out var name) ? name : null;

    /// <summary>
    ///     The export stem for one model, or null to keep its filename.
    ///
    ///     A gameplay entity is its own one-piece set — its two ids are equal — so its
    ///     set name IS the model's name and a piece suffix would only repeat it.
    /// </summary>
    public string? StemFor(uint idA, uint idB)
    {
        if (!Sets.TryGetValue(idA, out var set))
            return null;
        var stem = NdsSetNames.ToStem(set);
        if (idA == idB)
            return stem;
        return pieces.TryGetValue((idA, idB), out var piece)
            ? $"{stem}__{NdsSetNames.ToStem(piece)}"
            : $"{stem}__{idB:x8}";
    }
}
