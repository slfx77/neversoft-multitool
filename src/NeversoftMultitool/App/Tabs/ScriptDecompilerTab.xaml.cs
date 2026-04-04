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
    private static readonly string[] ScriptExtensions = [".trg", ".qb"];

    private readonly ScriptDecompilerDetailPresenter _detailPresenter;
    private readonly ScriptDecompilerTabExporter _exporter = new();
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<IListEntry> _parentFiles = [];
    private string _outputDir = string.Empty;

    public ScriptDecompilerTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += ScriptDecompilerTab_Unloaded;
        _detailPresenter = new ScriptDecompilerDetailPresenter(new ScriptDecompilerDetailView(
            DetailPanel,
            DetailSplitterColumn,
            DetailSplitter,
            DetailColumn,
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
        Unloaded -= ScriptDecompilerTab_Unloaded;
        _exporter.Dispose();
    }

    private async void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".trg");
        picker.FileTypeFilter.Add(".qb");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        InputPathText.Text = file.Path;
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        var ext = Path.GetExtension(file.Path).ToLowerInvariant();
        try
        {
            IListEntry entry = ext == ".qb"
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
        ClearDetail();
        _items.Clear();
        _parentFiles.Clear();

        var scriptFiles = Directory.GetFiles(path)
            .Where(f => ScriptExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in scriptFiles)
        {
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            IListEntry entry = ext == ".qb"
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
        OutputPathText.Text = _outputDir;
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

    private void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _detailPresenter.ShowSelection(FilesListView.SelectedItem);
    }

    private void ClearDetail()
    {
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

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        switch (button.Tag)
        {
            case TrgFileEntry trgParent:
                ExpandCollapseTrg(trgParent);
                break;
            case QbFileEntry qbParent:
                ExpandCollapseQb(qbParent);
                break;
        }
    }

    private void ExpandCollapseTrg(TrgFileEntry parent)
    {
        if (parent.NodeCount == 0) return;
        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            if (parent.CachedChildren == null)
            {
                if (parent.CachedParsedFile == null)
                {
                    try
                    {
                        parent.CachedParsedFile = TrgFile.Parse(parent.FilePath);
                        parent.NodeCount = parent.CachedParsedFile.NodeCount;
                        parent.VersionDisplay =
                            $"{parent.CachedParsedFile.VersionMajor}.{parent.CachedParsedFile.VersionMinor}";
                    }
                    catch
                    {
                        parent.CachedChildren = [];
                        return;
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

            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private void ExpandCollapseQb(QbFileEntry parent)
    {
        if (parent.NodeCount == 0) return;
        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            if (parent.CachedChildren == null)
            {
                if (parent.CachedParsedFile == null)
                {
                    try
                    {
                        parent.CachedParsedFile = QbFile.Parse(parent.FilePath);
                        parent.NodeCount = parent.CachedParsedFile.Items.Count;
                    }
                    catch
                    {
                        parent.CachedChildren = [];
                        return;
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

            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private void ScriptDecompilerTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
