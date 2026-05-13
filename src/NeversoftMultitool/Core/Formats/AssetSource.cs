namespace NeversoftMultitool.Core.Formats;

/// <summary>
///     Abstraction over where an asset's bytes and companion files come from. Lets
///     converters across every tab (Mesh, Audio, Texture, Bitmap, Video) work
///     uniformly whether the asset is a file on disk or an entry inside an
///     archive (WAD/PRE/PKR/PAK/...) with no temp-extraction step.
///     Companion lookups default to flat basename matching; filesystem sources
///     additionally search sibling subdirectories (TEX/, SKE/, Textures/, ...).
/// </summary>
public abstract class AssetSource
{
    /// <summary>Friendly identifier for logs and UI (e.g. path or "archive.wad::entry.psx").</summary>
    public abstract string DisplayName { get; }

    /// <summary>Basename of the asset entry, including compound extension (e.g. "skater.psx").</summary>
    public abstract string EntryName { get; }

    /// <summary>
    ///     Real disk path backing this source, or null if it's archive-backed.
    ///     A few legacy consumers still need the original path (e.g. to drive their
    ///     own discovery). New code should prefer the byte-reading APIs.
    /// </summary>
    public virtual string? FileSystemPath => null;

    /// <summary>Read the asset bytes.</summary>
    public abstract byte[] ReadBytes();

    /// <summary>Cheap existence check for a companion by basename. No bytes read.</summary>
    public abstract bool CompanionExists(string nameWithExtension);

    /// <summary>Read companion bytes by exact basename, or null if missing.</summary>
    public abstract byte[]? TryReadCompanion(string nameWithExtension);

    /// <summary>
    ///     Read companion bytes by stem + list of extensions to try, checking sibling
    ///     subdirectories as well. Filesystem sources honour <paramref name="subdirs" />;
    ///     archive sources ignore them (flat entry list).
    /// </summary>
    public abstract byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null);

    /// <summary>
    ///     For filesystem sources only: returns a real disk path to a companion so
    ///     callers that currently need paths (e.g. legacy discovery walkers) can
    ///     keep working. Archive sources return null — callers must fall back to
    ///     byte-reading APIs.
    /// </summary>
    public virtual string? TryResolveCompanionPath(string nameWithExtension)
    {
        return null;
    }

    /// <summary>
    ///     Path version of the stem+extensions lookup. Filesystem sources walk
    ///     configured subdirectories; archive sources return null.
    /// </summary>
    public virtual string? TryResolveCompanionPath(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null)
    {
        return null;
    }
}
