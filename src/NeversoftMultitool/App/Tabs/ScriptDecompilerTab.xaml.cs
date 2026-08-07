using System.Collections.ObjectModel;
using Windows.Storage.Pickers;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Trg;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class ScriptDecompilerTab : UserControl, IDisposable
{
    private static readonly string[] TrgExtensions = [".trg"];

    /// <summary>
    ///     Compiled script files: THPS3-THUG2 raw-token .qb plus THAW-generation
    ///     platform-suffixed sectioned QB (and .sqb sound-script) variants.
    /// </summary>
    private static readonly string[] QbFileSuffixes =
    [
        ".qb", ".qb.ps2", ".qb.wpc", ".qb.ngc", ".qb.xbx",
        ".sqb", ".sqb.ps2", ".sqb.wpc", ".sqb.ngc", ".sqb.xbx"
    ];

    private readonly ScriptDecompilerDetailPresenter _detailPresenter;
    private readonly ScriptDecompilerTabExporter _exporter = new();
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly ObservableCollection<IListEntry> _nodeItems = [];
    private readonly List<IListEntry> _parentFiles = [];
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

    private static bool IsQbPath(string path)
    {
        var name = Path.GetFileName(path);
        return QbFileSuffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsScriptPath(string path)
    {
        return IsQbPath(path) || IsTrgPath(path);
    }

    /// <summary>
    ///     A TRG is either a bare <c>.trg</c> or a platform-suffixed one — the
    ///     carved N64 triggers are <c>.trg.n64</c>. The reader takes the byte
    ///     order from the file's own magic, so both route to the same parser.
    /// </summary>
    private static bool IsTrgPath(string path)
    {
        var name = Path.GetFileName(path);
        return TrgExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()) ||
               name.Contains(".trg.", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        Unloaded -= ScriptDecompilerTab_Unloaded;
        _exporter.Dispose();
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".trg");
        picker.FileTypeFilter.Add(".qb");
        picker.FileTypeFilter.Add(".sqb");
        picker.FileTypeFilter.Add(".ps2");
        picker.FileTypeFilter.Add(".wpc");
        picker.FileTypeFilter.Add(".ngc");
        picker.FileTypeFilter.Add(".xbx");
        // Carved N64 triggers and model bundles.
        picker.FileTypeFilter.Add(".n64");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        InputPathText.Text = file.Path;
        if (Path.GetDirectoryName(file.Path) is { Length: > 0 } fileDir)
            DefaultOutputToInput(fileDir);
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        try
        {
            IListEntry entry = IsQbPath(file.Path)
                ? ParseQbFileEntry(file.Path)
                : ParseTrgFileEntry(file.Path);
            _parentFiles.Add(entry);
            _items.Add(entry);
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Failed to parse: {ex.Message}");
        }

        UpdateUiState();
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        InputPathText.Text = path;
        DefaultOutputToInput(path);
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        var scriptFiles = Directory.GetFiles(path)
            .Where(IsScriptPath)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in scriptFiles)
        {
            IListEntry entry = IsQbPath(filePath)
                ? new QbFileEntry
                {
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath
                }
                : new TrgFileEntry
                {
                    FileName = Path.GetFileName(filePath),
                    FilePath = filePath
                };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();

        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                switch (entry)
                {
                    case TrgFileEntry trg:
                        BackgroundParseTrg(trg, dispatcher);
                        break;
                    case QbFileEntry qb:
                        BackgroundParseQb(qb, dispatcher);
                        break;
                }
            }
        });
    }

    private static void BackgroundParseTrg(TrgFileEntry entry,
        DispatcherQueue dispatcher)
    {
        try
        {
            var trg = TrgFile.Parse(entry.FilePath);
            entry.CachedParsedFile = trg;
            dispatcher.TryEnqueue(() =>
            {
                entry.NodeCount = trg.NodeCount;
                entry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
            });
        }
        catch
        {
            dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Error);
        }
    }

    private static void BackgroundParseQb(QbFileEntry entry,
        DispatcherQueue dispatcher)
    {
        try
        {
            var qb = QbFile.Parse(entry.FilePath);
            entry.CachedParsedFile = qb;
            dispatcher.TryEnqueue(() =>
            {
                entry.NodeCount = qb.Items.Count;
                entry.VersionDisplay = "QB";
            });
        }
        catch
        {
            dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Error);
        }
    }

    private static TrgFileEntry ParseTrgFileEntry(string filePath)
    {
        var trg = TrgFile.Parse(filePath);
        var entry = new TrgFileEntry
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath
        };
        entry.CachedParsedFile = trg;
        entry.NodeCount = trg.NodeCount;
        entry.VersionDisplay = $"{trg.VersionMajor}.{trg.VersionMinor}";
        return entry;
    }

    private static QbFileEntry ParseQbFileEntry(string filePath)
    {
        var qb = QbFile.Parse(filePath);
        var entry = new QbFileEntry
        {
            FileName = Path.GetFileName(filePath),
            FilePath = filePath
        };
        entry.CachedParsedFile = qb;
        entry.NodeCount = qb.Items.Count;
        entry.VersionDisplay = "QB";
        return entry;
    }

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
                    parent.CachedParsedFile = TrgFile.Parse(parent.FilePath);
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
                    parent.CachedParsedFile = QbFile.Parse(parent.FilePath);
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
        await _exporter.CancelAsync(ExportButton, CancelButton);
    }

    private void ScriptDecompilerTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
