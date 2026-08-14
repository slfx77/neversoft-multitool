using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Script;

namespace NeversoftMultitool;

public sealed partial class ScriptDecompilerTab : UserControl, IDisposable
{
    private static readonly string[] ScriptPickerExtensions =
    [
        ".trg", ".qb", ".sqb",
        ".ps2", ".wpc", ".ngc", ".xbx", ".xen", ".n64"
    ];

    private readonly ScriptDecompilerDetailPresenter _detailPresenter;
    private readonly ScriptDecompilerTabExporter _exporter = new();
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly ObservableCollection<IListEntry> _nodeItems = [];
    private readonly List<IListEntry> _parentFiles = [];
    private CancellationTokenSource? _loadCts;
    private bool _disposed;
    private int _nodeListRequestId;
    private string _outputDir = string.Empty;
    private bool _outputManuallySet;

    public ScriptDecompilerTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        NodesListView.ItemsSource = _nodeItems;
        Unloaded += ScriptDecompilerTab_Unloaded;
        _detailPresenter = new ScriptDecompilerDetailPresenter(new ScriptDecompilerDetailView(
            DetailPlaceholderText,
            DetailTypeBadge,
            DetailTypeText,
            DetailIndexText,
            PropertiesSection,
            PropertiesGrid,
            PositionSection,
            DetailPositionText,
            AnglesSection,
            DetailAnglesText,
            LinksSection,
            DetailLinksText,
            CommandsSection,
            CommandsHeaderText,
            CommandsRepeater,
            ScriptSection,
            ScriptHeaderText,
            ScriptRepeater,
            RawHexSection,
            DetailRawHexText,
            SourceSection,
            SourceHeaderText,
            DetailSourceText));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unloaded -= ScriptDecompilerTab_Unloaded;
        _loadCts?.Cancel();
        _nodeListRequestId++;
        _exporter.Dispose();
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? cts = null;
        try
        {
            var path = await FilePickerHelper.PickFileAsync(ScriptPickerExtensions);
            if (path == null || _disposed) return;

            var loadCts = cts = BeginLoad(path, Path.GetDirectoryName(path));
            var kind = ScriptAssetParser.ClassifyEntryName(Path.GetFileName(path));
            if (kind == null)
                throw new InvalidDataException(
                    $"'{Path.GetFileName(path)}' does not have a supported script suffix.");

            var entry = await Task.Run(() =>
            {
                loadCts.Token.ThrowIfCancellationRequested();
                return CreateParsedEntry(new FileSystemAssetSource(path), kind.Value);
            }, loadCts.Token);

            if (!IsCurrentLoad(cts)) return;
            PublishEntries([entry]);
            MainWindow.Instance?.SetStatus($"Loaded {Path.GetFileName(path)}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == null || IsCurrentLoad(cts))
                MainWindow.Instance?.SetStatus($"Failed to parse: {ex.Message}");
        }
        finally
        {
            ReleaseLoad(cts);
        }
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? cts = null;
        try
        {
            var path = await FolderPickerHelper.PickFolderAsync();
            if (path == null || _disposed) return;

            var loadCts = cts = BeginLoad(path, path);
            var entries = await Task.Run(() =>
            {
                var loaded = new List<IListEntry>();
                var scripts = Directory.EnumerateFiles(path)
                    .Select(filePath => (
                        FilePath: filePath,
                        Kind: ScriptAssetParser.ClassifyEntryName(Path.GetFileName(filePath))))
                    .Where(static candidate => candidate.Kind != null)
                    .OrderBy(static candidate => Path.GetFileName(candidate.FilePath),
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static candidate => candidate.FilePath, StringComparer.Ordinal)
                    .ToArray();

                foreach (var (filePath, kind) in scripts)
                {
                    loadCts.Token.ThrowIfCancellationRequested();
                    loaded.Add(CreateEntryWithMetadata(
                        new FileSystemAssetSource(filePath), kind!.Value));
                }

                return loaded;
            }, loadCts.Token);

            if (!IsCurrentLoad(cts)) return;
            PublishEntries(entries);
            MainWindow.Instance?.SetStatus(entries.Count == 0
                ? $"{Path.GetFileName(path)}: no script files found."
                : $"Found {entries.Count} script file(s) in {Path.GetFileName(path)}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == null || IsCurrentLoad(cts))
                MainWindow.Instance?.SetStatus($"Failed to scan folder: {ex.Message}");
        }
        finally
        {
            ReleaseLoad(cts);
        }
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        CancellationTokenSource? cts = null;
        try
        {
            var path = await FilePickerHelper.PickFileAsync(ScriptArchiveCatalog.PickerExtensions);
            if (path == null || _disposed) return;

            var loadCts = cts = BeginLoad(path, Path.GetDirectoryName(path));
            var result = await Task.Run(() =>
            {
                using var catalog = ScriptArchiveCatalog.Open(path, loadCts.Token);
                var loaded = new List<IListEntry>(catalog.Candidates.Count);
                var unreadable = 0;

                foreach (var candidate in catalog.Candidates)
                {
                    loadCts.Token.ThrowIfCancellationRequested();
                    try
                    {
                        // Archive sources are lazy. Buffer each script before the
                        // catalog is disposed so preview/export never depends on a
                        // live archive handle after this load operation completes.
                        var source = ScriptAssetParser.Materialize(candidate.Source);
                        loaded.Add(CreateEntryWithMetadata(source, candidate.Kind));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or IOException
                                               or EndOfStreamException or ArgumentException
                                               or OverflowException)
                    {
                        unreadable++;
                    }
                }

                return new ArchiveLoadResult(loaded, unreadable);
            }, loadCts.Token);

            if (!IsCurrentLoad(cts)) return;
            PublishEntries(result.Entries);
            var archiveName = Path.GetFileName(path);
            var failureSuffix = result.UnreadableCount == 0
                ? string.Empty
                : $" ({result.UnreadableCount} unreadable entr{(result.UnreadableCount == 1 ? "y" : "ies")})";
            MainWindow.Instance?.SetStatus(result.Entries.Count == 0
                ? $"{archiveName}: no script files found{failureSuffix}."
                : $"Found {result.Entries.Count} script file(s) in {archiveName}{failureSuffix}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (cts == null || IsCurrentLoad(cts))
                MainWindow.Instance?.SetStatus($"Failed to scan archive: {ex.Message}");
        }
        finally
        {
            ReleaseLoad(cts);
        }
    }

    private CancellationTokenSource BeginLoad(string inputPath, string? defaultOutputDirectory)
    {
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;

        InputPathText.Text = inputPath;
        if (!string.IsNullOrEmpty(defaultOutputDirectory))
            DefaultOutputToInput(defaultOutputDirectory);
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();
        UpdateUiState();
        return cts;
    }

    private bool IsCurrentLoad(CancellationTokenSource cts) =>
        !_disposed && !cts.IsCancellationRequested && ReferenceEquals(_loadCts, cts);

    private void ReleaseLoad(CancellationTokenSource? cts)
    {
        if (cts == null) return;
        if (ReferenceEquals(_loadCts, cts))
            _loadCts = null;
        cts.Dispose();
    }

    private void PublishEntries(IEnumerable<IListEntry> entries)
    {
        foreach (var entry in entries)
        {
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();
    }

    private static IListEntry CreateEntryWithMetadata(AssetSource source, ScriptAssetKind kind)
    {
        var entry = CreateUnparsedEntry(source, kind);
        try
        {
            PopulateMetadata(entry);
        }
        catch
        {
            ((BaseFileEntry)entry).Status = ExtractionStatus.Error;
        }

        return entry;
    }

    private static IListEntry CreateParsedEntry(AssetSource source, ScriptAssetKind kind)
    {
        var entry = CreateUnparsedEntry(source, kind);
        PopulateMetadata(entry);
        return entry;
    }

    private static IListEntry CreateUnparsedEntry(AssetSource source, ScriptAssetKind kind) => kind switch
    {
        ScriptAssetKind.Qb => new QbFileEntry
        {
            FileName = source.EntryName,
            FilePath = source.DisplayName,
            Source = source
        },
        ScriptAssetKind.Trg => new TrgFileEntry
        {
            FileName = source.EntryName,
            FilePath = source.DisplayName,
            Source = source
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown script asset kind.")
    };

    private static void PopulateMetadata(IListEntry entry)
    {
        switch (entry)
        {
            case TrgFileEntry trgEntry:
                var trg = ScriptAssetParser.ParseTrg(trgEntry.Source);
                trgEntry.CachedParsedFile = trg;
                trgEntry.NodeCount = trg.NodeCount;
                trgEntry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
                break;
            case QbFileEntry qbEntry:
                var qb = ScriptAssetParser.ParseQb(qbEntry.Source);
                qbEntry.CachedParsedFile = qb;
                qbEntry.NodeCount = qb.Items.Count;
                qbEntry.VersionDisplay = "QB";
                break;
        }
    }

    private sealed record ArchiveLoadResult(
        IReadOnlyList<IListEntry> Entries,
        int UnreadableCount);

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        _outputManuallySet = true;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void DefaultOutputToInput(string dir)
    {
        if (_outputManuallySet)
            return;

        _outputDir = dir;
        OutputPathText.Text = dir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _parentFiles.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ExportButton.IsEnabled = hasFiles && hasOutput;
    }

    // ─── Selection: file → node list, node → detail ───────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = FilesListView.SelectedItem;
        _detailPresenter.ShowSelection(selected);

        var requestId = ++_nodeListRequestId;
        _nodeItems.Clear();
        NodesPlaceholderText.Text = "Select a file to list its nodes.";
        NodesPlaceholderText.Visibility = Visibility.Visible;

        List<IListEntry> children;
        switch (selected)
        {
            case TrgFileEntry trg:
                children = await Task.Run(() => GetTrgChildren(trg));
                if (requestId != _nodeListRequestId) return;
                RefreshTrgDisplay(trg);
                break;

            case QbFileEntry qb:
                children = await Task.Run(() => GetQbChildren(qb));
                if (requestId != _nodeListRequestId) return;
                RefreshQbDisplay(qb);
                break;

            default:
                return;
        }

        foreach (var child in children)
            _nodeItems.Add(child);

        NodesPlaceholderText.Text = children.Count > 0
            ? "Select a file to list its nodes."
            : "No nodes in this file.";
        NodesPlaceholderText.Visibility = children.Count > 0
            ? Visibility.Collapsed
            : Visibility.Visible;

        // The file detail may have been shown before the parse finished —
        // re-present it now that counts/version are known.
        if (ReferenceEquals(FilesListView.SelectedItem, selected))
            _detailPresenter.ShowSelection(selected);
    }

    private void NodesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NodesListView.SelectedItem is { } node)
            _detailPresenter.ShowSelection(node);
    }

    private static List<IListEntry> GetTrgChildren(TrgFileEntry parent)
    {
        if (parent.CachedChildren == null)
        {
            if (parent.CachedParsedFile == null)
            {
                try
                {
                    parent.CachedParsedFile = ScriptAssetParser.ParseTrg(parent.Source);
                }
                catch
                {
                    parent.CachedChildren = [];
                    return [];
                }
            }

            parent.CachedChildren = parent.CachedParsedFile.Nodes
                .Select((node, index) => new TrgNodeEntry
                {
                    ParentFileName = parent.FileName,
                    NodeIndex = index,
                    Node = node
                }).ToList();
        }

        return [.. parent.CachedChildren];
    }

    private static List<IListEntry> GetQbChildren(QbFileEntry parent)
    {
        if (parent.CachedChildren == null)
        {
            if (parent.CachedParsedFile == null)
            {
                try
                {
                    parent.CachedParsedFile = ScriptAssetParser.ParseQb(parent.Source);
                }
                catch
                {
                    parent.CachedChildren = [];
                    return [];
                }
            }

            parent.CachedChildren = parent.CachedParsedFile.Items
                .Select((item, index) => new QbItemEntry
                {
                    ParentFileName = parent.FileName,
                    ItemIndex = index,
                    Item = item,
                    QbFile = parent.CachedParsedFile
                }).ToList();
        }

        return [.. parent.CachedChildren];
    }

    private static void RefreshTrgDisplay(TrgFileEntry entry)
    {
        if (entry.CachedParsedFile is not { } trg) return;
        entry.NodeCount = trg.NodeCount;
        entry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
    }

    private static void RefreshQbDisplay(QbFileEntry entry)
    {
        if (entry.CachedParsedFile is not { } qb) return;
        entry.NodeCount = qb.Items.Count;
        entry.VersionDisplay = "QB";
    }

    private void ClearDetail()
    {
        _nodeListRequestId++;
        _nodeItems.Clear();
        NodesPlaceholderText.Text = "Select a file to list its nodes.";
        NodesPlaceholderText.Visibility = Visibility.Visible;
        _detailPresenter.Clear();
    }

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        await _exporter.ExportAsync(
            _parentFiles,
            _outputDir,
            DispatcherQueue,
            ExportButton,
            CancelButton,
            ExportProgress);
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _exporter.CancelAsync();
    }

    private void ScriptDecompilerTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
