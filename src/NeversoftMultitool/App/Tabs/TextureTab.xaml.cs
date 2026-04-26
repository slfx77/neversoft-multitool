using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public sealed partial class TextureTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

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
        Unloaded += TextureTab_Unloaded;
    }

    public void Dispose()
    {
        Unloaded -= TextureTab_Unloaded;
        _cts?.Dispose();
        _cts = null;
        _previewCts?.Dispose();
        _previewCts = null;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        _items.Clear();
        _parentFiles.Clear();
        var candidateFiles = Directory.EnumerateFiles(_inputDir, "*", SearchOption.AllDirectories)
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
                unsupported.Add(new ScanSummaryDialog.UnsupportedFile(Path.GetFileName(file)!,
                    probe.UnsupportedReason ?? "Unknown format"));
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
                Source = new FileSystemAssetSource(file),
                RelativePath = MakeRelativePath(file, _inputDir),
                Format = TextureTabTextureOperations.ClassifyFormat(fileName)
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();
        RunCountEnumeration();
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;

        _items.Clear();
        _parentFiles.Clear();

        await Task.Run(() =>
        {
            var backend = ArchiveAssetBackend.TryOpen(path);
            if (backend == null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.SetStatus($"{Path.GetFileName(path)}: unsupported archive.");
                    UpdateUiState();
                });
                return;
            }

            var archiveName = Path.GetFileName(path);
            var entries = new List<PsxFileEntry>();
            foreach (var archiveEntry in backend.Entries)
            {
                if (!TextureTabTextureOperations.IsTextureFile(archiveEntry.Name)) continue;
                entries.Add(new PsxFileEntry
                {
                    FileName = archiveEntry.Name,
                    Source = new ArchiveAssetSource(backend, archiveEntry),
                    RelativePath = $"{archiveName}::{archiveEntry.Name}",
                    Format = TextureTabTextureOperations.ClassifyFormat(archiveEntry.Name)
                });
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in entries)
                {
                    _parentFiles.Add(entry);
                    _items.Add(entry);
                }

                MainWindow.Instance?.SetStatus(entries.Count == 0
                    ? $"{archiveName}: no texture entries."
                    : $"Found {entries.Count} texture entrie(s) in {archiveName}.");

                UpdateUiState();
                RunCountEnumeration();
            });
        });
    }

    private void RunCountEnumeration()
    {
        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    var count = TextureTabTextureOperations.CountTextures(entry.Source, entry.Format);
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

    private static string MakeRelativePath(string file, string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir)) return Path.GetFileName(file);
        try { return Path.GetRelativePath(rootDir, file); }
        catch { return Path.GetFileName(file); }
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
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            if (parent.CachedChildren == null)
            {
                try
                {
                    parent.CachedChildren = TextureTabTextureOperations.EnumerateChildren(
                        parent.Source,
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

        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        var createSubDirs = CreateSubDirsCheckbox.IsChecked == true;
        var writeDds = WriteDdsCheckbox.IsChecked == true;
        var writeMipAtlas = WriteMipAtlasCheckbox.IsChecked == true;

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
        var token = cts.Token;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;

        var entries = _parentFiles.ToList();

        await Task.Run(() =>
        {
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = entries.Select(entry => Task.Run(async () =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var (totalTex, writtenTex, skipped, success) =
                        TextureTabTextureOperations.ExtractTextures(
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

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var cts = _cts;
        if (cts != null)
        {
            _cts = null;
            await cts.CancelAsync();
            cts.Dispose();
        }

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
        var glyph = _sortAscending ? "" : "";
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
        var previousPreviewCts = _previewCts;
        if (previousPreviewCts != null)
        {
            _previewCts = null;
            await previousPreviewCts.CancelAsync();
            previousPreviewCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewPanel.Visibility = Visibility.Visible;
        PreviewSplitter.Visibility = Visibility.Visible;
        SplitterColumn.Width = new GridLength(8);
        if (PreviewColumn.Width.Value <= 0)
            PreviewColumn.Width = new GridLength(280);
        TexturePreview.Source = null;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";

        var parent = _parentFiles.FirstOrDefault(p =>
            p.FileName.Equals(texture.ParentFileName, StringComparison.OrdinalIgnoreCase));
        if (parent == null)
        {
            PreviewLoading.IsActive = false;
            NoPreviewIcon.Visibility = Visibility.Visible;
            PreviewDimensionsText.Text = "Parent not found";
            return;
        }

        var source = parent.Source;
        var nameHash = texture.NameHash;
        var format = parent.Format;

        var result = await Task.Run(
            () => TextureTabTextureOperations.GetPreviewRgba(source, nameHash, format),
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
        _previewCts?.Dispose();
        _previewCts = null;
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewSplitter.Visibility = Visibility.Collapsed;
        SplitterColumn.Width = new GridLength(0);
        PreviewColumn.Width = new GridLength(0);
        TexturePreview.Source = null;
        PreviewLoading.IsActive = false;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";
    }

    private void TextureTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
