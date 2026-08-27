using System.Collections.ObjectModel;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool;

/// <summary>
///     The one mesh scan the window holds, shared by the Levels tab and the
///     Meshes &amp; Characters tab.
/// </summary>
/// <remarks>
///     Scanning walks archives and probes file content, so it is far too expensive
///     to run twice. Both tabs publish into this session and each renders its own
///     <see cref="MeshScanSlice" /> of it, which also means a row can never appear
///     in both lists bound to the same mutable entry.
/// </remarks>
internal sealed class MeshTabScanSession
{
    /// <summary>Window-scoped: the tabs are created once and never leave the tree.</summary>
    public static MeshTabScanSession Instance { get; } = new();

    private List<MeshFileEntry> _entries = [];

    /// <summary>What was scanned, for the tabs' path boxes and status lines.</summary>
    public string SourcePath { get; private set; } = "";

    /// <summary>Everything the last scan found, in scan order.</summary>
    public IReadOnlyList<MeshFileEntry> Entries => _entries;

    /// <summary>Raised on the UI thread after <see cref="Publish" /> or <see cref="Clear" />.</summary>
    public event Action? Changed;

    /// <summary>Replace the session's contents with a completed scan's result.</summary>
    public void Publish(string sourcePath, IEnumerable<MeshFileEntry> entries)
    {
        SourcePath = sourcePath;
        _entries = [.. entries];
        Changed?.Invoke();
    }

    /// <summary>Drop the current scan (a new one is starting, or it failed).</summary>
    public void Clear()
    {
        SourcePath = "";
        _entries = [];
        Changed?.Invoke();
    }

    /// <summary>
    ///     Refill <paramref name="target" /> with this session's rows for
    ///     <paramref name="slice" />, preserving scan order.
    /// </summary>
    /// <remarks>
    ///     Order matters beyond appearances: <c>MeshOutputPathPlanner</c> resolves
    ///     colliding output stems by first-seen ordinal, so a reordered list would
    ///     rename converted files.
    /// </remarks>
    public void FillSlice(MeshScanSlice slice, ObservableCollection<MeshFileEntry> target)
    {
        target.Clear();
        foreach (var entry in _entries)
        {
            if (MeshScanSlicing.Includes(slice, entry.LevelFacts))
                target.Add(entry);
        }
    }

    /// <summary>How many rows the other tab is holding, for a "N are over there" hint.</summary>
    public int CountIn(MeshScanSlice slice) =>
        _entries.Count(entry => MeshScanSlicing.Includes(slice, entry.LevelFacts));
}
