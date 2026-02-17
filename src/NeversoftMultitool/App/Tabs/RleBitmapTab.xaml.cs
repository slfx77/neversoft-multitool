using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Rle;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class RleBitmapTab : UserControl
{
    private readonly ObservableCollection<RleFileEntry> _files = [];
    private string _inputDir = "";
    private string _outputDir = "";

    public RleBitmapTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _files;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _inputDir = folder.Path;
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
            _files.Add(new RleFileEntry { FileName = file! });
        }

        UpdateUiState();
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _outputDir = folder.Path;
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

    private void AutoWidthCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        WidthNumberBox.IsEnabled = AutoWidthCheckbox.IsChecked != true;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        var autoDetect = AutoWidthCheckbox.IsChecked == true;
        var width = (int)WidthNumberBox.Value;

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
                var result = autoDetect
                    ? RleImage.Convert(inputFile)
                    : RleImage.Convert(inputFile, width);

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
}
