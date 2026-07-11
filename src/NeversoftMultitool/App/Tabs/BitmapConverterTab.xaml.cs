using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool;

public sealed partial class RleBitmapTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

    private readonly ObservableCollection<RleFileEntry> _files = [];
    private CancellationTokenSource? _debounceCts;
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _previewCts;
    private bool _suppressWidthEvents;

    public RleBitmapTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _files;
        Unloaded += RleBitmapTab_Unloaded;
    }

    public void Dispose()
    {
        Unloaded -= RleBitmapTab_Unloaded;
        _debounceCts?.Dispose();
        _debounceCts = null;
        _previewCts?.Dispose();
        _previewCts = null;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        _files.Clear();

        await Task.Run(() =>
        {
            var rleFiles = Directory.EnumerateFiles(_inputDir, "*", SearchOption.AllDirectories)
                .Where(IsSupportedBitmapFile)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var entries = rleFiles.Select(file => new RleFileEntry
            {
                FileName = Path.GetFileName(file),
                Source = new FileSystemAssetSource(file),
                RelativePath = MakeRelativePath(file, _inputDir),
                DetectedWidth = RleImage.DetectWidth(file)
            }).ToList();

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in entries)
                    _files.Add(entry);
                UpdateUiState();
            });
        });
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;

        _files.Clear();

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
            var entries = new List<RleFileEntry>();
            foreach (var archiveEntry in backend.Entries)
            {
                var name = archiveEntry.Name;
                if (!IsSupportedBitmapFile(name))
                    continue;

                var source = new ArchiveAssetSource(backend, archiveEntry);
                var detectedWidth = RleImage.DetectWidth(source.ReadBytes(), name);
                entries.Add(new RleFileEntry
                {
                    FileName = name,
                    Source = source,
                    RelativePath = $"{archiveName}::{name}",
                    DetectedWidth = detectedWidth
                });
            }

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in entries)
                    _files.Add(entry);
                MainWindow.Instance?.SetStatus(entries.Count == 0
                    ? $"{archiveName}: no .rle/.bmr entries."
                    : $"Found {entries.Count} bitmap entrie(s) in {archiveName}.");
                UpdateUiState();
            });
        });
    }

    private static bool IsSupportedBitmapFile(string path)
        => path.EndsWith(".rle", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".bmr", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".zlb", StringComparison.OrdinalIgnoreCase);

    private static string MakeRelativePath(string file, string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir)) return Path.GetFileName(file);
        try
        {
            return Path.GetRelativePath(rootDir, file);
        }
        catch
        {
            return Path.GetFileName(file);
        }
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
        var hasFiles = _files.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private async void AutoWidthCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        var isAuto = AutoWidthCheckbox.IsChecked == true;
        WidthNumberBox.IsEnabled = !isAuto;

        if (_suppressWidthEvents) return;
        if (FilesListView.SelectedItem is not RleFileEntry entry) return;

        _suppressWidthEvents = true;
        if (isAuto)
        {
            entry.WidthOverride = null;
            WidthNumberBox.Value = entry.DetectedWidth;
        }
        else
        {
            entry.WidthOverride = (int)WidthNumberBox.Value;
        }

        _suppressWidthEvents = false;

        await LoadBitmapPreview(entry);
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        foreach (var file in _files)
        {
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.IsEnabled = false;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _files.Count;
        var outputDir = _outputDir;
        var dispatcher = DispatcherQueue;

        await Task.Run(() =>
        {
            foreach (var entry in _files)
            {
                dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                var result = RleImage.Convert(entry.Source.ReadBytes(), entry.FileName,
                    entry.WidthOverride ?? entry.EffectiveWidth);

                if (result.Success)
                {
                    var outputFile = Path.Combine(outputDir,
                        Path.GetFileNameWithoutExtension(entry.FileName) + ".png");
                    ImageWriter.WritePngRgb(outputFile, result.Width, result.Height, result.RgbPixels);
                }

                var status = result.Success ? ExtractionStatus.Done : ExtractionStatus.Error;
                var processed = Interlocked.Increment(ref filesProcessed);

                dispatcher.TryEnqueue(() =>
                {
                    entry.Status = status;
                    ConversionProgress.Value = (double)processed / totalFiles * 100;
                });
            }
        });

        stopwatch.Stop();
        ConversionProgress.Value = 100;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus($"Completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previousDebounceCts = _debounceCts;
        if (previousDebounceCts != null)
        {
            _debounceCts = null;
            await previousDebounceCts.CancelAsync();
            previousDebounceCts.Dispose();
        }

        if (FilesListView.SelectedItem is RleFileEntry entry)
        {
            _suppressWidthEvents = true;
            if (entry.WidthOverride.HasValue)
            {
                AutoWidthCheckbox.IsChecked = false;
                WidthNumberBox.IsEnabled = true;
                WidthNumberBox.Value = entry.WidthOverride.Value;
            }
            else
            {
                AutoWidthCheckbox.IsChecked = true;
                WidthNumberBox.IsEnabled = false;
                WidthNumberBox.Value = entry.DetectedWidth;
            }

            _suppressWidthEvents = false;

            await LoadBitmapPreview(entry);
        }
        else
        {
            ClearPreview();
        }
    }

    private async Task LoadBitmapPreview(RleFileEntry entry)
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
        BitmapPreview.Source = null;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";

        var source = entry.Source;
        var fileName = entry.FileName;
        var width = entry.EffectiveWidth;

        var result = await Task.Run(
            () => RleImage.Convert(source.ReadBytes(), fileName, width),
            cts.Token);

        if (cts.Token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;

        if (result.Success)
        {
            BitmapPreview.Source = BitmapHelper.CreateFromRgb(result.Width, result.Height, result.RgbPixels);
            PreviewDimensionsText.Text = $"{result.Width} x {result.Height}";
            PreviewInfoText.Text = entry.WidthOverride.HasValue ? "Manual width override" : "Width auto-detected";
        }
        else
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            PreviewDimensionsText.Text = result.ErrorMessage ?? "Failed to decode";
        }
    }

    private async void WidthNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_suppressWidthEvents) return;
        if (double.IsNaN(args.NewValue)) return;
        if (FilesListView.SelectedItem is not RleFileEntry entry) return;
        if (AutoWidthCheckbox.IsChecked == true) return;

        entry.WidthOverride = (int)args.NewValue;

        var previousDebounceCts = _debounceCts;
        if (previousDebounceCts != null)
        {
            _debounceCts = null;
            await previousDebounceCts.CancelAsync();
            previousDebounceCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _debounceCts = cts;

        try
        {
            await Task.Delay(300, cts.Token);
            if (!cts.Token.IsCancellationRequested)
                await LoadBitmapPreview(entry);
        }
        catch (TaskCanceledException)
        {
            // Debounce cancelled — newer value incoming
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
        BitmapPreview.Source = null;
        PreviewLoading.IsActive = false;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";
    }

    private void RleBitmapTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
