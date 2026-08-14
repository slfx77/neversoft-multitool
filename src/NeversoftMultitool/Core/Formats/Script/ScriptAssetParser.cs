using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Script;

internal enum ScriptAssetKind
{
    Qb,
    Trg
}

/// <summary>
///     Shared script-name policy and byte-based parser entry points. Keeping the
///     source abstraction here lets callers treat filesystem files and archive
///     entries identically, without extracting archive entries to temporary files.
/// </summary>
internal static class ScriptAssetParser
{
    private static readonly string[] PlatformSuffixes =
    [
        ".ps2", ".wpc", ".ngc", ".xbx", ".xen", ".n64"
    ];

    public static bool IsCandidateEntryName(string name) => ClassifyEntryName(name) != null;

    /// <summary>
    ///     Reads a source exactly once and returns a self-contained in-memory
    ///     source with the same UI identity. This lets archive callers dispose
    ///     the owning catalog before publishing rows or starting later exports.
    ///     Script parsing has no companion-file dependency, so companion lookups
    ///     on the returned source intentionally report no matches.
    /// </summary>
    public static AssetSource Materialize(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new BufferedScriptAssetSource(
            source.DisplayName,
            source.EntryName,
            source.ReadBytes());
    }

    public static ScriptAssetKind? ClassifyEntryName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (HasScriptSuffix(name, ".qb") || HasScriptSuffix(name, ".sqb"))
            return ScriptAssetKind.Qb;

        return HasScriptSuffix(name, ".trg") ? ScriptAssetKind.Trg : null;
    }

    public static QbFile ParseQb(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return QbFile.Parse(source.ReadBytes(), source.EntryName);
    }

    public static TrgFile ParseTrg(AssetSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bytes = source.ReadBytes();
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream);
        return TrgFile.Parse(reader, source.EntryName);
    }

    private static bool HasScriptSuffix(string name, string scriptSuffix)
    {
        if (name.EndsWith(scriptSuffix, StringComparison.OrdinalIgnoreCase))
            return true;

        return PlatformSuffixes.Any(platformSuffix =>
            name.EndsWith(scriptSuffix + platformSuffix, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class BufferedScriptAssetSource(
    string displayName,
    string entryName,
    byte[] bytes) : AssetSource
{
    public override string DisplayName => displayName;
    public override string EntryName => entryName;

    public override byte[] ReadBytes() => bytes;

    public override bool CompanionExists(string nameWithExtension) => false;

    public override byte[]? TryReadCompanion(string nameWithExtension) => null;

    public override byte[]? TryReadCompanion(
        string stem,
        IReadOnlyList<string> extensions,
        IReadOnlyList<string>? subdirs = null) => null;
}
