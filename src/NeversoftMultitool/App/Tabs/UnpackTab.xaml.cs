using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;

namespace NeversoftMultitool;

public sealed partial class UnpackTab : UserControl, IDisposable
{
    private readonly ObservableCollection<UnpackArchiveEntry> _archives = [];
    private CancellationTokenSource? _cts;
    private string _rootDir = "";

    public UnpackTab()
    {
        InitializeComponent();
        ArchivesListView.ItemsSource = _archives;
        Unloaded += UnpackTab_Unloaded;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _rootDir = path;
        InputPathText.Text = _rootDir;
        _archives.Clear();

        var rootDir = _rootDir;
        var dispatcher = DispatcherQueue;

        await Task.Run(() =>
        {
            var discovered = RecursiveUnpacker.Scan(rootDir);
            dispatcher.TryEnqueue(() =>
            {
                foreach (var info in discovered)
                {
                    _archives.Add(new UnpackArchiveEntry
                    {
                        FilePath = info.FilePath,
                        RelativePath = Path.GetRelativePath(rootDir, info.FilePath),
                        ArchiveType = info.ArchiveType,
                        Pass = info.Pass,
                        Status = info.AlreadyExtracted
                            ? ExtractionStatus.Skipped
                            : ExtractionStatus.Pending
                    });
                }

                UpdateStats();
                UpdateUiState();
            });
        });

        EmptyStatePanel.Visibility = Visibility.Collapsed;
        FileListCard.Visibility = Visibility.Visible;
        StatsCard.Visibility = Visibility.Visible;
    }

    private void UpdateStats()
    {
        TotalArchivesText.Text = _archives.Count.ToString();
        PendingArchivesText.Text = _archives.Count(a => a.Status == ExtractionStatus.Pending).ToString();
        SkippedArchivesText.Text = _archives.Count(a => a.Status == ExtractionStatus.Skipped).ToString();
    }

    private void UpdateUiState()
    {
        UnpackButton.IsEnabled = _archives.Any(a => a.Status == ExtractionStatus.Pending);
    }

    private async void UnpackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_archives.Count == 0 || string.IsNullOrEmpty(_rootDir)) return;

        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;
        UnpackButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        UnpackProgress.Visibility = Visibility.Visible;
        UnpackProgress.Value = 0;

        var rootDir = _rootDir;
        var token = cts.Token;
        var dispatcher = DispatcherQueue;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await Task.Run(() =>
            {
                RecursiveUnpacker.ExtractAll(
                    rootDir,
                    onArchiveStarted: archive =>
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            var entry = FindOrAddEntry(archive, rootDir);
                            entry.Status = ExtractionStatus.Processing;
                        });
                    },
                    onArchiveCompleted: archive =>
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            var entry = FindEntry(archive.FilePath);
                            if (entry != null)
                            {
                                entry.Status = archive.Error != null
                                    ? ExtractionStatus.Error
                                    : ExtractionStatus.Done;
                                entry.ErrorMessage = archive.Error;
                            }

                            UpdateProgress();
                            UpdateStats();
                        });
                    },
                    onPassDiscovered: (pass, newArchives) =>
                    {
                        dispatcher.TryEnqueue(() =>
                        {
                            foreach (var info in newArchives)
                            {
                                if (FindEntry(info.FilePath) != null)
                                    continue;

                                _archives.Add(new UnpackArchiveEntry
                                {
                                    FilePath = info.FilePath,
                                    RelativePath = Path.GetRelativePath(rootDir, info.FilePath),
                                    ArchiveType = info.ArchiveType,
                                    Pass = pass,
                                    Status = ExtractionStatus.Pending
                                });
                            }

                            UpdateStats();
                        });
                    },
                    ct: token);
            }, token);

            stopwatch.Stop();
            UnpackProgress.Value = 100;
            var doneCount = _archives.Count(a => a.Status == ExtractionStatus.Done);
            var errorCount = _archives.Count(a => a.Status == ExtractionStatus.Error);
            MainWindow.Instance?.SetStatus(
                $"Unpacked {doneCount} archives" +
                (errorCount > 0 ? $" ({errorCount} errors)" : "") +
                $" in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Unpack cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Error: {ex.Message}");
        }
        finally
        {
            DisposeCancellationTokenSource();
            CancelButton.Visibility = Visibility.Collapsed;
            UpdateUiState();
        }
    }

    private UnpackArchiveEntry? FindEntry(string filePath) =>
        _archives.FirstOrDefault(a =>
            a.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

    private UnpackArchiveEntry FindOrAddEntry(RecursiveUnpacker.ArchiveInfo info, string rootDir)
    {
        var existing = FindEntry(info.FilePath);
        if (existing != null) return existing;

        var entry = new UnpackArchiveEntry
        {
            FilePath = info.FilePath,
            RelativePath = Path.GetRelativePath(rootDir, info.FilePath),
            ArchiveType = info.ArchiveType,
            Pass = info.Pass
        };
        _archives.Add(entry);
        return entry;
    }

    private void UpdateProgress()
    {
        var total = _archives.Count(a => a.Status != ExtractionStatus.Skipped);
        var done = _archives.Count(a =>
            a.Status is ExtractionStatus.Done or ExtractionStatus.Error);
        UnpackProgress.Value = total > 0 ? (double)done / total * 100 : 0;
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
        UpdateUiState();
        MainWindow.Instance?.SetStatus("Unpack cancelled");
    }

    public void Dispose()
    {
        Unloaded -= UnpackTab_Unloaded;
        DisposeCancellationTokenSource();
    }

    private void UnpackTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private void DisposeCancellationTokenSource()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
