using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Archives;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class ArchiveExtractorTab : UserControl
{
    private readonly ObservableCollection<ArchiveFileEntry> _files = [];
    private string _archivePath = "";
    private string _outputDir = "";
    private string _archiveType = "";
    private CancellationTokenSource? _cts;

    public ArchiveExtractorTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _files;
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".wad");
        picker.FileTypeFilter.Add(".pkr");
        picker.FileTypeFilter.Add(".pre");
        picker.FileTypeFilter.Add(".ddx");
        picker.FileTypeFilter.Add(".bon");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        _archivePath = file.Path;
        InputPathText.Text = _archivePath;
        var ext = Path.GetExtension(_archivePath).ToLowerInvariant();

        _files.Clear();

        try
        {
            List<ArchiveEntry> entries;

            switch (ext)
            {
                case ".wad":
                    _archiveType = "WAD";
                    entries = WadArchive.GetFileList(_archivePath);
                    break;
                case ".pkr":
                    _archiveType = "PKR3";
                    entries = PkrArchive.GetFileList(_archivePath);
                    break;
                case ".pre":
                    _archiveType = "PRE";
                    entries = PreArchive.GetFileList(_archivePath);
                    break;
                case ".ddx":
                    _archiveType = "DDX";
                    entries = DdxArchive.GetFileList(_archivePath);
                    break;
                case ".bon":
                    _archiveType = "BON";
                    entries = BonArchive.GetFileList(_archivePath);
                    break;
                default:
                    MainWindow.Instance?.SetStatus($"Unsupported archive format: {ext}");
                    return;
            }

            foreach (var entry in entries)
            {
                _files.Add(new ArchiveFileEntry
                {
                    FileName = entry.FullName,
                    Size = entry.Size
                });
            }

            ArchiveNameText.Text = Path.GetFileName(_archivePath);
            ArchiveTypeText.Text = _archiveType;
            ArchiveFileCountText.Text = entries.Count.ToString();

            ArchiveInfoCard.Visibility = Visibility.Visible;
            EmptyStatePanel.Visibility = Visibility.Collapsed;
            FileListCard.Visibility = Visibility.Visible;
        }
        catch (NotSupportedException ex)
        {
            MainWindow.Instance?.SetStatus(ex.Message);
            return;
        }
        catch (FileNotFoundException ex)
        {
            MainWindow.Instance?.SetStatus(ex.Message);
            return;
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Error loading archive: {ex.Message}");
            return;
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

        ExtractButton.IsEnabled = hasFiles && hasOutput;
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || string.IsNullOrEmpty(_outputDir) || string.IsNullOrEmpty(_archivePath))
            return;

        _cts = new CancellationTokenSource();

        // Reset state
        foreach (var file in _files)
        {
            file.Status = ExtractionStatus.Pending;
        }

        ExtractButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var archivePath = _archivePath;
        var outputDir = _outputDir;
        var archiveType = _archiveType;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var fileEntries = _files;

        try
        {
            await Task.Run(() =>
            {
                Action<int, int> onProgress = (current, total) =>
                {
                    dispatcher.TryEnqueue(() =>
                    {
                        if (current - 1 < fileEntries.Count)
                            fileEntries[current - 1].Status = ExtractionStatus.Done;
                        if (current < fileEntries.Count)
                            fileEntries[current].Status = ExtractionStatus.Processing;

                        ExtractionProgress.Value = (double)current / total * 100;
                    });
                };

                switch (archiveType)
                {
                    case "WAD":
                        WadArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "PKR3":
                        PkrArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "DDX":
                        DdxArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "BON":
                        BonArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "PRE":
                        PreArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                }
            }, token);

            // Mark all remaining as done
            foreach (var file in fileEntries)
            {
                if (file.Status == ExtractionStatus.Pending || file.Status == ExtractionStatus.Processing)
                    file.Status = ExtractionStatus.Done;
            }

            stopwatch.Stop();
            ExtractionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Extracted {fileEntries.Count} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Extraction cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ExtractButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ExtractButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Extraction cancelled");
    }
}
