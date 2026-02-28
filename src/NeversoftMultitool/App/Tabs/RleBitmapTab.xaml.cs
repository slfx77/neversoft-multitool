using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Rle;

namespace NeversoftMultitool;

public sealed partial class RleBitmapTab : UserControl
{
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
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        _files.Clear();
        var rleFiles = Directory.GetFiles(_inputDir)
            .Where(f => f.EndsWith(".rle", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".bmr", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in rleFiles)
        {
            var filePath = Path.Combine(_inputDir, file!);
            var detectedWidth = RleImage.DetectWidth(filePath);
            _files.Add(new RleFileEntry { FileName = file!, DetectedWidth = detectedWidth });
        }

        UpdateUiState();
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

        // Reset state
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
        var inputDir = _inputDir;
        var outputDir = _outputDir;
        var dispatcher = DispatcherQueue;

        await Task.Run(() =>
        {
            foreach (var entry in _files)
            {
                dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                var inputFile = Path.Combine(inputDir, entry.FileName);
                var result = RleImage.Convert(inputFile, entry.EffectiveWidth);

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
        _debounceCts?.Cancel();

        if (FilesListView.SelectedItem is RleFileEntry entry)
        {
            // Sync width controls to the selected file's state
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
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        // Show the preview panel and loading state
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(280);
        BitmapPreview.Source = null;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";

        var inputFile = Path.Combine(_inputDir, entry.FileName);
        var width = entry.EffectiveWidth;

        var result = await Task.Run(() => RleImage.Convert(inputFile, width), cts.Token);

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

        // Debounce preview re-rendering
        _debounceCts?.Cancel();
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
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        BitmapPreview.Source = null;
        PreviewLoading.IsActive = false;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";
    }
}
