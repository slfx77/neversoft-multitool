using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

public sealed partial class MeshConverterTab : UserControl
{
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private CancellationTokenSource? _cts;
    private string _inputDir = "";
    private string _outputDir = "";

    public MeshConverterTab()
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
        ConvertButton.IsEnabled = false;

        var scanSummary = await Task.Run(() => MeshConverterTabFileScanner.AnalyzeDirectory(_inputDir));
        if (scanSummary.UnsupportedFiles.Count > 0)
        {
            var proceed = await ScanSummaryDialog.ShowIfNeeded(
                XamlRoot,
                scanSummary.SupportedCount,
                [.. scanSummary.UnsupportedFiles]);
            if (!proceed)
                return;
        }

        var entries = await Task.Run(() => MeshConverterTabFileScanner.ScanDirectory(_inputDir));
        foreach (var entry in entries)
            _items.Add(entry);

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
        var hasFiles = _items.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _items)
        {
            file.TriangleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalTriangles = 0;
        var totalConverted = 0;
        var totalFiles = _items.Count;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;
        var embedTextures = EmbedTexturesCheckbox.IsChecked == true;
        var token = _cts.Token;
        var entries = _items.ToList();

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested)
                    break;

                dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                try
                {
                    var triangles = MeshConverterTabFileConverter.ConvertFile(entry, outputDir, embedTextures);
                    Interlocked.Add(ref totalTriangles, triangles);
                    Interlocked.Increment(ref totalConverted);

                    var processed = Interlocked.Increment(ref filesProcessed);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TriangleCount = triangles;
                        entry.Status = ExtractionStatus.Done;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
                catch
                {
                    var processed = Interlocked.Increment(ref filesProcessed);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = ExtractionStatus.Error;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        ConversionProgress.Value = 100;
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus(
            $"Converted {totalConverted}/{totalFiles} files " +
            $"({totalTriangles:N0} triangles) in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }
}
