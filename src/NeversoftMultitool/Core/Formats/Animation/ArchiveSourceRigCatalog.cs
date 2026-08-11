using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One selectable skeleton inside an archive. The source
///     retains the exact backend/entry identity; <see cref="DisplayName" /> is
///     the full virtual path and therefore does not collapse same-named entries
///     in different directories or nested containers.
/// </summary>
internal sealed record ArchiveSourceRigCandidate(
    ArchiveAssetSource Source,
    string DisplayName);

/// <summary>
///     Bounded archive browser shared by the explicit animation- and mesh-rig
///     pickers. Each caller supplies its own exact entry-name policy; the
///     original animation overload deliberately remains narrower than the
///     general skeleton loader.
///     The catalog owns the root backend and every nested backend it opens.
///     Keep it alive through source parsing and validation, then dispose it;
///     parsed rig objects are self-contained.
/// </summary>
internal sealed class ArchiveSourceRigCatalog : IDisposable
{
    private static readonly HashSet<string> EnumerableArchiveExtensions =
    [
        ".wad", ".pre", ".prx", ".prd", ".prf", ".prg",
        ".pkr", ".pak", ".apk"
    ];

    private readonly List<ArchiveAssetBackend> _ownedBackends;
    private bool _disposed;

    private ArchiveSourceRigCatalog(
        List<ArchiveAssetBackend> ownedBackends,
        IReadOnlyList<ArchiveSourceRigCandidate> candidates)
    {
        _ownedBackends = ownedBackends;
        Candidates = candidates;
    }

    /// <summary>
    ///     Final suffixes needed by FileOpenPicker. Platform suffixes expose
    ///     compound containers such as global_s.apk.ngc and scripts.pak.wpc;
    ///     ArchiveTypeDetector/ArchiveAssetBackend still perform strict content
    ///     validation after selection.
    /// </summary>
    public static IReadOnlyList<string> PickerExtensions { get; } =
        ArchiveTypeDetector.ArchiveExtensions
            .Where(EnumerableArchiveExtensions.Contains)
            .Concat([".ps2", ".ngc", ".wpc", ".xbx", ".xen"])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static extension => extension, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public IReadOnlyList<ArchiveSourceRigCandidate> Candidates { get; }

    /// <summary>
    ///     The GUI contract intentionally remains narrower than the general
    ///     skeleton loader: Xbox/WPC mesh-rig selection is a separate feature,
    ///     so .ske.xbx must not appear here merely because the core loader can
    ///     parse it.
    /// </summary>
    public static bool IsCandidateEntryName(string name) =>
        name.EndsWith(".ske", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ske.ps2", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".ske.ngc", StringComparison.OrdinalIgnoreCase);

    public static ArchiveSourceRigCatalog Open(
        string archivePath,
        CancellationToken cancellationToken = default) =>
        Open(archivePath, IsCandidateEntryName, cancellationToken);

    /// <summary>
    ///     Opens the same bounded catalog with a caller-owned exact entry-name
    ///     policy. Mesh-rig selection passes the general skeleton loader's
    ///     policy so .ske.xbx is admitted without widening animation sources.
    /// </summary>
    public static ArchiveSourceRigCatalog Open(
        string archivePath,
        Func<string, bool> isCandidateEntryName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentNullException.ThrowIfNull(isCandidateEntryName);
        cancellationToken.ThrowIfCancellationRequested();

        var root = ArchiveAssetBackend.TryOpen(archivePath)
                   ?? throw new InvalidDataException(
                       $"'{Path.GetFileName(archivePath)}' is not a supported enumerable archive.");
        var owned = new List<ArchiveAssetBackend> { root };

        try
        {
            var candidates = new List<ArchiveSourceRigCandidate>();
            var pending = new Queue<ArchiveAssetBackend>();
            pending.Enqueue(root);

            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var backend = pending.Dequeue();
                foreach (var entry in backend.Entries)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (isCandidateEntryName(entry.Name))
                    {
                        var source = new ArchiveAssetSource(backend, entry);
                        candidates.Add(new ArchiveSourceRigCandidate(source, source.DisplayName));
                    }

                    var nested = backend.TryOpenNested(entry);
                    if (nested == null) continue;
                    owned.Add(nested);
                    pending.Enqueue(nested);
                }
            }

            var ordered = candidates
                .OrderBy(static candidate => candidate.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static candidate => candidate.DisplayName, StringComparer.Ordinal)
                .ToArray();
            return new ArchiveSourceRigCatalog(owned, ordered);
        }
        catch
        {
            DisposeBackends(owned);
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        DisposeBackends(_ownedBackends);
    }

    private static void DisposeBackends(List<ArchiveAssetBackend> backends)
    {
        // Children retain reload closures into their parents. Dispose in the
        // opposite order they were opened so the root handle is always last.
        for (var index = backends.Count - 1; index >= 0; index--)
            backends[index].FileSystem.Dispose();
    }
}
