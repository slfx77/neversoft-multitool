using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;

namespace NeversoftMultitool;

public sealed partial class TextureTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr", ".z64"];

    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<PsxFileEntry> _parentFiles = [];
    private CancellationTokenSource? _cts;
    private bool _disposed;
    private string _inputDir = "";
    private string _outputDir = "";
    private bool _outputManuallySet;
    private CancellationTokenSource? _previewCts;
    private CancellationTokenSource? _scanCts;

    public TextureTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += TextureTab_Unloaded;
    }

    public void Dispose()
    {
        _disposed = true;
        Unloaded -= TextureTab_Unloaded;
        // Detach before disposing the CTSes: teardown clears the list, which
        // fires SelectionChanged and would re-enter the preview path.
        FilesListView.SelectionChanged -= FilesListView_SelectionChanged;
        _cts?.Dispose();
        _cts = null;
        _previewCts?.Dispose();
        _previewCts = null;
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;
        DefaultOutputToInput(path);

        await RunScan(
            $"Scanning {Path.GetFileName(path)}...",
            (progress, token) => TextureTabFileScanner.ScanDirectory(path, progress, token),
            "files probed");
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);

        await RunScan(
            $"Scanning {Path.GetFileName(path)}...",
            (progress, token) => TextureTabFileScanner.ScanArchive(path, progress, token),
            "entries found");
    }

    /// <summary>
    ///     Shared scan driver: enumeration + probing run off-thread with progress
    ///     (the old folder path did everything ON the UI thread — a 10k-file THUG
    ///     tree froze the app for minutes with no indication), then results
    ///     bulk-add on the dispatcher.
    /// </summary>
    private async Task RunScan(
        string statusPrefix,
        Func<IProgress<int>, CancellationToken, TextureTabFileScanner.ScanResult> scan,
        string progressNoun)
    {
        var previousScanCts = _scanCts;
        if (previousScanCts != null)
        {
            _scanCts = null;
            await previousScanCts.CancelAsync();
            previousScanCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        _items.Clear();
        _parentFiles.Clear();
        UpdateUiState();
        SetScanIndicator(true, statusPrefix);

        try
        {
            var progress = new Progress<int>(count =>
                SetScanIndicator(true, $"{statusPrefix} {count} {progressNoun}"));

            var result = await Task.Run(() => scan(progress, token), token);
            if (_disposed || token.IsCancellationRequested) return;

            if (result.Unsupported.Count > 0)
            {
                var proceed = await ScanSummaryDialog.ShowIfNeeded(
                    XamlRoot, result.Supported.Count, result.Unsupported);
                if (!proceed) return;
            }

            foreach (var entry in result.Supported)
            {
                _parentFiles.Add(entry);
                _items.Add(entry);
            }

            MainWindow.Instance?.SetStatus(result.Supported.Count == 0
                ? "No texture files found."
                : $"Found {result.Supported.Count} texture file(s).");

            UpdateUiState();
            RunCountEnumeration();
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer scan or torn down.
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (!_disposed)
                SetScanIndicator(false, "");
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
        }
    }

    private void SetScanIndicator(bool scanning, string statusText)
    {
        ScanProgressRing.IsActive = scanning;
        ScanProgressRing.Visibility = scanning ? Visibility.Visible : Visibility.Collapsed;
        if (scanning && statusText.Length > 0)
            MainWindow.Instance?.SetStatus(statusText);
    }

    private void RunCountEnumeration()
    {
        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            // Counting parses every file, so it parallelizes; UI updates flush in
            // batches instead of one dispatcher hop per file.
            var pendingUpdates = new ConcurrentQueue<(PsxFileEntry Entry, int Count)>();

            void Flush()
            {
                if (pendingUpdates.IsEmpty) return;
                var batch = new List<(PsxFileEntry Entry, int Count)>();
                while (pendingUpdates.TryDequeue(out var update))
                    batch.Add(update);
                dispatcher.TryEnqueue(() =>
                {
                    foreach (var (entry, count) in batch)
                    {
                        entry.TextureCount = count;
                        entry.HasTextures = count > 0;
                    }
                });
            }

            var processed = 0;
            Parallel.ForEach(
                entries,
                new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount },
                entry =>
                {
                    int count;
                    try
                    {
                        count = TextureTabTextureOperations.CountTextures(entry.Source, entry.Format);
                    }
                    catch
                    {
                        count = 0;
                    }

                    pendingUpdates.Enqueue((entry, count));
                    if (Interlocked.Increment(ref processed) % 64 == 0)
                        Flush();
                });

            Flush();
        });
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
        ExtractButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PsxFileEntry parent }) return;
        if (!parent.IsExpandable) return;

        if (parent.IsExpanded)
            CollapseEntry(parent);
        else
            ExpandEntry(parent);
    }

    private void ExpandEntry(PsxFileEntry parent)
    {
        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0 || parent.IsExpanded) return;

        EnsureChildren(parent);
        if (parent.CachedChildren!.Count == 1)
        {
            // Single-texture file discovered on first expand (count enumeration
            // may not have landed yet): flatten it — the row previews directly —
            // instead of inserting a one-child sublist.
            parent.TextureCount = 1;
            return;
        }

        parent.IsExpanded = true;
        for (var i = 0; i < parent.CachedChildren!.Count; i++)
            _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
    }

    private void CollapseEntry(PsxFileEntry parent)
    {
        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0 || !parent.IsExpanded) return;

        parent.IsExpanded = false;
        var removeIndex = parentIndex + 1;
        while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
            _items.RemoveAt(removeIndex);
    }

    private static void EnsureChildren(PsxFileEntry parent)
    {
        if (parent.CachedChildren != null) return;

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

    private async void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        // Children enumerate lazily (each parse opens the file) — build them
        // off-thread, then rebuild the flat list in one pass.
        var parents = _parentFiles.Where(p => p.HasTextures).ToList();
        await Task.Run(() =>
        {
            foreach (var parent in parents)
                EnsureChildren(parent);
        });

        _items.Clear();
        foreach (var parent in _parentFiles)
        {
            _items.Add(parent);
            if (!parent.HasTextures || parent.CachedChildren is not { Count: > 1 } children)
                continue;

            parent.IsExpanded = true;
            foreach (var child in children)
                _items.Add(child);
        }
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        foreach (var parent in _parentFiles)
            parent.IsExpanded = false;

        _items.Clear();
        foreach (var parent in _parentFiles)
            _items.Add(parent);
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

        ExtractButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        using var scope = GlobalProgress.Begin("Extracting textures");

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
                        scope.Report(filesProcessed, totalFiles);
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
        ExtractButton.Visibility = Visibility.Visible;
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
        ExtractButton.Visibility = Visibility.Visible;
        MainWindow.Instance?.SetStatus("Extraction cancelled");
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        switch (FilesListView.SelectedItem)
        {
            case PsxTextureEntry texture:
                await LoadTexturePreview(texture);
                return;

            case PsxFileEntry { HasTextures: true } parent:
                // One click on a file: expand it (multi-texture only) and preview
                // its first texture WITHOUT moving the ListView selection —
                // reassigning SelectedItem onto a just-inserted, not-yet-realized
                // container forces a visible re-realize pass over the panel (the
                // whole list flashes) and re-enters this handler. Deferred past
                // this handler: mutating the bound collection while the ListView
                // is still dispatching SelectionChanged makes it regenerate every
                // container; after it unwinds the inserts apply incrementally,
                // same as the chevron path.
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (_disposed) return;

                    await Task.Run(() => EnsureChildren(parent));
                    if (_disposed) return;

                    if (!parent.IsExpanded)
                        ExpandEntry(parent);

                    if (parent.CachedChildren is { Count: > 0 } children)
                        await LoadTexturePreview(children[0]);
                });
                return;

            default:
                ClearPreview();
                return;
        }
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
        // Snapshot the token before any await: window close disposes _previewCts
        // (the same object as this local) mid-flight, and the
        // CancellationTokenSource.Token GETTER throws ObjectDisposedException —
        // the token struct itself stays safe to read.
        var token = cts.Token;

        TexturePreview.SetLoading(true);
        PreviewInfoText.Text = "";

        var parent = _parentFiles.FirstOrDefault(p =>
            p.FileName.Equals(texture.ParentFileName, StringComparison.OrdinalIgnoreCase));
        if (parent == null)
        {
            TexturePreview.Clear();
            PreviewInfoText.Text = "Parent not found";
            return;
        }

        var source = parent.Source;
        var nameHash = texture.NameHash;
        var format = parent.Format;

        try
        {
            var result = await Task.Run(
                () => TextureTabTextureOperations.GetPreviewRgba(source, nameHash, format),
                token);

            if (_disposed || token.IsCancellationRequested) return;

            if (result != null)
            {
                var (rgba, width, height) = result.Value;
                TexturePreview.SetSource(BitmapHelper.CreateFromRgba(width, height, rgba));
                PreviewInfoText.Text = $"{texture.PaletteType}\n{texture.NameDisplay}";
            }
            else
            {
                TexturePreview.Clear();
                PreviewInfoText.Text = "Failed to decode";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer selection or torn down mid-decode.
        }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        TexturePreview.Clear();
        PreviewInfoText.Text = "";
    }

    private void TextureTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
