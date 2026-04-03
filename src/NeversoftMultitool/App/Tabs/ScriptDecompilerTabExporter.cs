using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool;

internal sealed class ScriptDecompilerTabExporter : IDisposable
{
    private CancellationTokenSource? _cts;

    public async Task ExportAsync(
        IReadOnlyList<IListEntry> parentFiles,
        string outputDir,
        DispatcherQueue dispatcher,
        Button exportButton,
        Button cancelButton,
        ProgressBar exportProgress)
    {
        if (parentFiles.Count == 0 || string.IsNullOrEmpty(outputDir))
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

        foreach (var file in parentFiles)
        {
            if (file is BaseFileEntry baseEntry)
                baseEntry.Status = ExtractionStatus.Pending;
        }

        exportButton.IsEnabled = false;
        cancelButton.Visibility = Visibility.Visible;
        exportProgress.Visibility = Visibility.Visible;
        exportProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = parentFiles.Count;
        var token = cts.Token;
        var entries = parentFiles.ToList();

        try
        {
            await Task.Run(() =>
            {
                Directory.CreateDirectory(outputDir);

                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested)
                        break;

                    switch (entry)
                    {
                        case TrgFileEntry trg:
                            ExportTrgFile(trg, outputDir, dispatcher, exportProgress, ref filesProcessed, totalFiles);
                            break;
                        case QbFileEntry qb:
                            ExportQbFile(qb, outputDir, dispatcher, exportProgress, ref filesProcessed, totalFiles);
                            break;
                    }
                }
            }, token);

            stopwatch.Stop();
            exportProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Exported {filesProcessed} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Export cancelled");
        }
        finally
        {
            DisposeCancellationTokenSource();
            cancelButton.Visibility = Visibility.Collapsed;
            exportButton.IsEnabled = true;
        }
    }

    public async Task CancelAsync(Button exportButton, Button cancelButton)
    {
        var cts = _cts;
        if (cts != null)
        {
            _cts = null;
            await cts.CancelAsync();
            cts.Dispose();
        }

        cancelButton.Visibility = Visibility.Collapsed;
        exportButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Export cancelled");
    }

    public void Dispose()
    {
        DisposeCancellationTokenSource();
    }

    private void DisposeCancellationTokenSource()
    {
        _cts?.Dispose();
        _cts = null;
    }

    private static void ExportTrgFile(
        TrgFileEntry entry,
        string outputDir,
        DispatcherQueue dispatcher,
        ProgressBar exportProgress,
        ref int filesProcessed,
        int totalFiles)
    {
        dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

        try
        {
            var trg = entry.CachedParsedFile ?? TrgFile.Parse(entry.FilePath);
            entry.CachedParsedFile ??= trg;

            var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".json");
            trg.WriteJson(outputPath);

            var processed = Interlocked.Increment(ref filesProcessed);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Done;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
        catch
        {
            var processed = Interlocked.Increment(ref filesProcessed);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Error;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
    }

    private static void ExportQbFile(
        QbFileEntry entry,
        string outputDir,
        DispatcherQueue dispatcher,
        ProgressBar exportProgress,
        ref int filesProcessed,
        int totalFiles)
    {
        dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

        try
        {
            var qb = entry.CachedParsedFile ?? QbFile.Parse(entry.FilePath);
            entry.CachedParsedFile ??= qb;

            var outputPath = Path.Combine(outputDir, Path.GetFileNameWithoutExtension(entry.FileName) + ".q");
            var source = QbDecompiler.Decompile(qb);
            File.WriteAllText(outputPath, source);

            var processed = Interlocked.Increment(ref filesProcessed);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Done;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
        catch
        {
            var processed = Interlocked.Increment(ref filesProcessed);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Error;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
    }
}
