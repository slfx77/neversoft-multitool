using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Qb;
using NeversoftMultitool.Core.Formats.Script;

namespace NeversoftMultitool;

internal sealed class ScriptDecompilerTabExporter : IDisposable
{
    private readonly object _operationGate = new();
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public void Dispose()
    {
        lock (_operationGate)
        {
            _disposed = true;
            // The running ExportAsync invocation owns disposal and UI cleanup.
            _cts?.Cancel();
        }
    }

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

        CancellationTokenSource cts;
        lock (_operationGate)
        {
            if (_disposed || _cts != null)
            {
                // The visible export button is normally enough to prevent reentry;
                // this also closes programmatic, event-ordering, and post-disposal
                // races.
                return;
            }

            cts = new CancellationTokenSource();
            _cts = cts;
        }

        var ownsControls = false;
        try
        {
            foreach (var file in parentFiles)
            {
                if (file is BaseFileEntry baseEntry)
                    baseEntry.Status = ExtractionStatus.Pending;
            }

            exportButton.Visibility = Visibility.Collapsed;
            cancelButton.Visibility = Visibility.Visible;
            exportProgress.Visibility = Visibility.Visible;
            exportProgress.Value = 0;
            ownsControls = true;

            using var scope = GlobalProgress.Begin("Exporting scripts");

            var stopwatch = Stopwatch.StartNew();
            var filesProcessed = 0;
            var filesSucceeded = 0;
            var filesFailed = 0;
            var totalFiles = parentFiles.Count;
            var token = cts.Token;
            var entries = parentFiles.ToList();
            var outputPaths = ScriptOutputPathPlanner.Plan(entries
                    .Select(static entry => entry switch
                    {
                        QbFileEntry qb => new ScriptOutputPathInput(qb.FilePath, ScriptOutputKind.Qb),
                        TrgFileEntry trg => new ScriptOutputPathInput(trg.FilePath, ScriptOutputKind.Trg),
                        _ => throw new InvalidOperationException(
                            $"Unsupported script entry type '{entry.GetType().Name}'.")
                    })
                    .ToArray())
                .Select(outputName => Path.Combine(outputDir, outputName))
                .ToArray();

            await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(outputDir);

                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    token.ThrowIfCancellationRequested();

                    var entry = entries[entryIndex];
                    var outputPath = outputPaths[entryIndex];
                    switch (entry)
                    {
                        case TrgFileEntry trg:
                            ExportTrgFile(
                                trg, outputPath, dispatcher, exportProgress, scope,
                                ref filesProcessed, ref filesSucceeded, ref filesFailed,
                                totalFiles);
                            break;
                        case QbFileEntry qb:
                            ExportQbFile(
                                qb, outputPath, dispatcher, exportProgress, scope,
                                ref filesProcessed, ref filesSucceeded, ref filesFailed,
                                totalFiles);
                            break;
                    }
                }

                // Cancellation can arrive while the final file is being written.
                token.ThrowIfCancellationRequested();
            }, token);

            token.ThrowIfCancellationRequested();
            stopwatch.Stop();
            exportProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Export complete: {filesSucceeded} succeeded, {filesFailed} failed " +
                $"in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Export cancelled");
        }
        finally
        {
            lock (_operationGate)
            {
                if (ReferenceEquals(_cts, cts))
                {
                    // Keep the operation gate held until its controls are restored;
                    // a replacement export cannot acquire ownership in between.
                    if (ownsControls)
                    {
                        cancelButton.Visibility = Visibility.Collapsed;
                        exportButton.Visibility = Visibility.Visible;
                    }

                    _cts = null;
                    cts.Dispose();
                }
            }
        }
    }

    public Task CancelAsync()
    {
        lock (_operationGate)
        {
            // ExportAsync keeps both CTS and UI ownership until its worker exits.
            _cts?.Cancel();
        }

        return Task.CompletedTask;
    }

    private static void ExportTrgFile(
        TrgFileEntry entry,
        string outputPath,
        DispatcherQueue dispatcher,
        ProgressBar exportProgress,
        IGlobalProgressScope scope,
        ref int filesProcessed,
        ref int filesSucceeded,
        ref int filesFailed,
        int totalFiles)
    {
        dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

        try
        {
            var trg = entry.CachedParsedFile ?? ScriptAssetParser.ParseTrg(entry.Source);
            entry.CachedParsedFile ??= trg;

            trg.WriteJson(outputPath);

            Interlocked.Increment(ref filesSucceeded);
            var processed = Interlocked.Increment(ref filesProcessed);
            scope.Report(processed, totalFiles);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Done;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
        catch
        {
            Interlocked.Increment(ref filesFailed);
            var processed = Interlocked.Increment(ref filesProcessed);
            scope.Report(processed, totalFiles);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Error;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
    }

    private static void ExportQbFile(
        QbFileEntry entry,
        string outputPath,
        DispatcherQueue dispatcher,
        ProgressBar exportProgress,
        IGlobalProgressScope scope,
        ref int filesProcessed,
        ref int filesSucceeded,
        ref int filesFailed,
        int totalFiles)
    {
        dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

        try
        {
            var qb = entry.CachedParsedFile ?? ScriptAssetParser.ParseQb(entry.Source);
            entry.CachedParsedFile ??= qb;

            var source = QbDecompiler.Decompile(qb);
            File.WriteAllText(outputPath, source);

            Interlocked.Increment(ref filesSucceeded);
            var processed = Interlocked.Increment(ref filesProcessed);
            scope.Report(processed, totalFiles);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Done;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
        catch
        {
            Interlocked.Increment(ref filesFailed);
            var processed = Interlocked.Increment(ref filesProcessed);
            scope.Report(processed, totalFiles);
            dispatcher.TryEnqueue(() =>
            {
                entry.Status = ExtractionStatus.Error;
                exportProgress.Value = (double)processed / totalFiles * 100;
            });
        }
    }
}
