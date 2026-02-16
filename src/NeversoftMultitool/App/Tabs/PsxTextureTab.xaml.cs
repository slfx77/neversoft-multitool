using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Psx;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class PsxTextureTab : UserControl
{
    private readonly ObservableCollection<PsxFileEntry> _files = [];
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _cts;
    private string _sortColumn = "";
    private bool _sortAscending = true;

    public PsxTextureTab()
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
        var psxFiles = Directory.GetFiles(_inputDir)
            .Where(f => f.EndsWith(".psx", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Where(f => f != null)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);

        foreach (var file in psxFiles)
        {
            _files.Add(new PsxFileEntry { FileName = file! });
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
        ExtractButton.IsEnabled = hasFiles && hasOutput;
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();
        var createSubDirs = CreateSubDirsCheckbox.IsChecked == true;

        // Reset state
        foreach (var file in _files)
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
        var totalFiles = _files.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;

        await Task.Run(() =>
        {
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = _files.Select((entry, index) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var inputFile = Path.Combine(_inputDir, entry.FileName);
                    var result = PsxLibrary.ExtractTextures(inputFile, _outputDir, createSubDirs);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TextureCount = result.TotalTextures;
                        entry.ExtractedCount = result.TexturesWritten;
                        entry.Status = result.Skipped ? ExtractionStatus.Skipped
                            : result.Success ? ExtractionStatus.Done
                            : ExtractionStatus.Error;

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

    private void SortByFileName_Click(object sender, RoutedEventArgs e) =>
        SortFiles("FileName", f => f.FileName);

    private void SortByTextures_Click(object sender, RoutedEventArgs e) =>
        SortFiles("Textures", f => f.TextureCount);

    private void SortByExtracted_Click(object sender, RoutedEventArgs e) =>
        SortFiles("Extracted", f => f.ExtractedCount);

    private void SortByStatus_Click(object sender, RoutedEventArgs e) =>
        SortFiles("Status", f => (int)f.Status);

    private void SortFiles<T>(string column, Func<PsxFileEntry, T> keySelector)
    {
        if (_files.Count == 0) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        var sorted = _sortAscending
            ? _files.OrderBy(keySelector).ToList()
            : _files.OrderByDescending(keySelector).ToList();

        _files.Clear();
        foreach (var item in sorted)
            _files.Add(item);

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
}
