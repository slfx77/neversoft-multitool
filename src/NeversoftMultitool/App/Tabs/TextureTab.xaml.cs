using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;

namespace NeversoftMultitool;

public sealed partial class TextureTab : UserControl
{
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<PsxFileEntry> _parentFiles = [];
    private CancellationTokenSource? _cts;
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _previewCts;
    private bool _sortAscending = true;
    private string _sortColumn = "";

    public TextureTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        _items.Clear();
        _parentFiles.Clear();
        var candidateFiles = Directory.GetFiles(_inputDir)
            .Where(TextureTabTextureOperations.IsTextureFile)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Probe files for format support
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        var supported = new List<string>();
        foreach (var file in candidateFiles)
        {
            var probe = FormatProbe.ProbeTexture(file);
            if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                unsupported.Add(new(Path.GetFileName(file)!, probe.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(file);
        }

        if (unsupported.Count > 0)
        {
            var proceed = await ScanSummaryDialog.ShowIfNeeded(
                XamlRoot, supported.Count, unsupported);
            if (!proceed) return;
        }

        foreach (var file in supported)
        {
            var fileName = Path.GetFileName(file)!;
            var entry = new PsxFileEntry
            {
                FileName = fileName,
                Format = TextureTabTextureOperations.ClassifyFormat(fileName)
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();

        // Enumerate texture counts in background
        var inputDir = _inputDir;
        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    var inputFile = Path.Combine(inputDir, entry.FileName);
                    var count = TextureTabTextureOperations.CountTextures(inputFile, entry.Format);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TextureCount = count;
                        entry.HasTextures = count > 0;
                    });
                }
                catch
                {
                    dispatcher.TryEnqueue(() => entry.HasTextures = false);
                }
            }
        });
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
        ExtractButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PsxFileEntry parent }) return;
        if (!parent.HasTextures) return;

        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            // Collapse: remove children after parent
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            // Expand: lazy-load children on first expand
            if (parent.CachedChildren == null)
            {
                var inputFile = Path.Combine(_inputDir, parent.FileName);
                try
                {
                    parent.CachedChildren = TextureTabTextureOperations.EnumerateChildren(
                        inputFile,
                        parent.FileName,
                        parent.Format);
                }
                catch
                {
                    parent.CachedChildren = [];
                }
            }

            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();
        var createSubDirs = CreateSubDirsCheckbox.IsChecked == true;
        var writeDds = WriteDdsCheckbox.IsChecked == true;
        var writeMipAtlas = WriteMipAtlasCheckbox.IsChecked == true;

        // Reset state
        foreach (var file in _parentFiles)
        {
            file.TextureCount = 0;
            file.ExtractedCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ExtractButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _parentFiles.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var inputDir = _inputDir;
        var outputDir = _outputDir;

        // Snapshot parent entries for parallel iteration
        var entries = _parentFiles.ToList();

        await Task.Run(() =>
        {
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = entries.Select((entry, index) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var inputFile = Path.Combine(inputDir, entry.FileName);
                    var (totalTex, writtenTex, skipped, success) =
                        TextureTabTextureOperations.ExtractTextures(
                            inputFile,
                            entry,
                            outputDir,
                            createSubDirs,
                            writeDds,
                            writeMipAtlas);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TextureCount = totalTex;
                        entry.ExtractedCount = writtenTex;
                        entry.Status = (skipped, success) switch
                        {
                            (true, _) => ExtractionStatus.Skipped,
                            (_, true) => ExtractionStatus.Done,
                            _ => ExtractionStatus.Error
                        };

                        filesProcessed++;
                        ExtractionProgress.Value = (double)filesProcessed / totalFiles * 100;
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, token)).ToArray();

            Task.WaitAll(tasks, token);
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        ExtractionProgress.Value = 100;
        CancelButton.Visibility = Visibility.Collapsed;
        ExtractButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus($"Completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ExtractButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Extraction cancelled");
    }

    private void SortByFileName_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("FileName", f => f.FileName);
    }

    private void SortByTextures_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Textures", f => f.TextureCount);
    }

    private void SortByExtracted_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Extracted", f => f.ExtractedCount);
    }

    private void SortByStatus_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Status", f => (int)f.Status);
    }

    private void SortFiles<T>(string column, Func<PsxFileEntry, T> keySelector)
    {
        if (_parentFiles.Count == 0) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        // Collapse all before sorting
        foreach (var parent in _parentFiles)
            parent.IsExpanded = false;

        var sorted = _sortAscending
            ? _parentFiles.OrderBy(keySelector).ToList()
            : _parentFiles.OrderByDescending(keySelector).ToList();

        _parentFiles.Clear();
        _parentFiles.AddRange(sorted);

        _items.Clear();
        foreach (var item in sorted)
            _items.Add(item);

        UpdateSortIcons();
    }

    private void UpdateSortIcons()
    {
        var glyph = _sortAscending ? "\uE70E" : "\uE70D";
        FileNameSortIcon.Glyph = _sortColumn == "FileName" ? glyph : "";
        TexturesSortIcon.Glyph = _sortColumn == "Textures" ? glyph : "";
        ExtractedSortIcon.Glyph = _sortColumn == "Extracted" ? glyph : "";
        StatusSortIcon.Glyph = _sortColumn == "Status" ? glyph : "";
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesListView.SelectedItem is PsxTextureEntry texture)
            await LoadTexturePreview(texture);
        else
            ClearPreview();
    }

    private async Task LoadTexturePreview(PsxTextureEntry texture)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(280);
        TexturePreview.Source = null;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";

        var inputFile = Path.Combine(_inputDir, texture.ParentFileName);
        var nameHash = texture.NameHash;
        var format = TextureTabTextureOperations.ClassifyFormat(texture.ParentFileName);

        var result = await Task.Run(
            () => TextureTabTextureOperations.GetPreviewRgba(inputFile, nameHash, format),
            cts.Token);

        if (cts.Token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;

        if (result != null)
        {
            var (rgba, width, height) = result.Value;
            TexturePreview.Source = BitmapHelper.CreateFromRgba(width, height, rgba);
            PreviewDimensionsText.Text = $"{width} x {height}";
            PreviewInfoText.Text = $"{texture.PaletteType}\n{texture.NameDisplay}";
        }
        else
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            PreviewDimensionsText.Text = "Failed to decode";
        }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        TexturePreview.Source = null;
        PreviewLoading.IsActive = false;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";
    }
}
