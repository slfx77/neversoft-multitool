using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Storage.Pickers;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.DiscImage;
using NeversoftMultitool.Core.Formats.N64;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class ArchiveExtractorTab : UserControl, IDisposable
{
    private readonly ObservableCollection<ArchiveFileEntry> _files = [];
    private string _archivePath = "";
    private string _archiveType = "";
    private CancellationTokenSource? _cts;
    private string _outputDir = "";
    private bool _outputManuallySet;

    public ArchiveExtractorTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _files;
        Unloaded += ArchiveExtractorTab_Unloaded;
    }

    public void Dispose()
    {
        Unloaded -= ArchiveExtractorTab_Unloaded;
        DisposeCancellationTokenSource();
    }

    private async void OpenArchiveButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        foreach (var extension in ArchiveExtractorRoute.PickerExtensions)
            picker.FileTypeFilter.Add(extension);
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        _archivePath = file.Path;
        InputPathText.Text = _archivePath;
        var ext = ArchiveExtractorRoute.ResolveExtension(_archivePath);

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
                case ".pre" when CompressedPreArchive.IsCompressedPre(_archivePath):
                case ".prd" when CompressedPreArchive.IsCompressedPre(_archivePath):
                case ".prf" when CompressedPreArchive.IsCompressedPre(_archivePath):
                case ".prg" when CompressedPreArchive.IsCompressedPre(_archivePath):
                case ".prx":
                    _archiveType = "PRE3";
                    entries = CompressedPreArchive.GetFileList(_archivePath);
                    break;
                case ".pre":
                case ".prd":
                case ".prf":
                case ".prg":
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
                case ".zip" or ".wpc" or ".ngc" when QZipArchive.IsZip(_archivePath):
                    _archiveType = "ZIP";
                    entries = QZipArchive.GetFileList(_archivePath);
                    break;
                case ".cut" or ".ps2" or ".xbx" when CutArchive.IsCut(_archivePath):
                    _archiveType = "CUT";
                    entries = CutArchive.GetFileList(_archivePath);
                    break;
                case ".pak" or ".apk" or ".ps2" or ".ngc" when PakArchive.IsPakArchive(_archivePath):
                    _archiveType = "PAK";
                    entries = PakArchive.GetFileList(_archivePath);
                    break;
                case ".iso" or ".cue" or ".gdi" or ".img" or ".bin"
                    when DiscImageArchive.IsDiscImage(_archivePath):
                    _archiveType = "DISC";
                    entries = DiscImageArchive.GetFileList(_archivePath);
                    break;
                case ".z64" when N64RomArchive.IsN64Rom(_archivePath):
                    _archiveType = "N64";
                    entries = N64RomArchive.GetFileList(_archivePath);
                    break;
                case ".z64":
                    // .v64/.n64 byte orders and non-ROM files get the precise
                    // re-dump message instead of a generic failure.
                    MainWindow.Instance?.SetStatus(
                        N64RomArchive.ClassifyRom(_archivePath) ?? "Not an N64 ROM");
                    return;
                default:
                    var probe = FormatProbe.ProbeArchive(_archivePath);
                    var reason = probe.UnsupportedReason ?? $"Unsupported archive format: {ext}";
                    MainWindow.Instance?.SetStatus(reason);
                    return;
            }

            foreach (var entry in entries)
            {
                _files.Add(new ArchiveFileEntry
                {
                    FileName = entry.Name,
                    Folder = entry.Directory.Replace('\\', '/'),
                    Size = entry.Size
                });
            }

            // Default the output next to the archive unless the user chose one.
            if (!_outputManuallySet && Path.GetDirectoryName(_archivePath) is { Length: > 0 } archiveDir)
            {
                _outputDir = archiveDir;
                OutputPathText.Text = archiveDir;
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
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        _outputManuallySet = true;
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

        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        // Reset state
        foreach (var file in _files)
        {
            file.Status = ExtractionStatus.Pending;
        }

        ExtractButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var archivePath = _archivePath;
        var outputDir = _outputDir;
        var archiveType = _archiveType;
        var token = cts.Token;
        var dispatcher = DispatcherQueue;
        var fileEntries = _files;

        var scope = GlobalProgress.Begin($"Extracting {archiveType} archive");

        try
        {
            await Task.Run(() =>
            {
                Action<int, int> onProgress = (current, total) =>
                {
                    scope.Report(current, total);
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
                    case "PRE3":
                        CompressedPreArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "PRE":
                        PreArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "PAK":
                        PakArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "ZIP":
                        QZipArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "CUT":
                        CutArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "DISC":
                        DiscImageArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
                        break;
                    case "N64":
                        N64RomArchive.ExtractFiles(archivePath, outputDir, onProgress, token);
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
            scope.Dispose();
            DisposeCancellationTokenSource();
            CancelButton.Visibility = Visibility.Collapsed;
            ExtractButton.Visibility = Visibility.Visible;
        }
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

    private void ArchiveExtractorTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void DisposeCancellationTokenSource()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
