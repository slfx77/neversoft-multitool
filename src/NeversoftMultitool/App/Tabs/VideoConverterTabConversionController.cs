using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class VideoConverterTabConversionController : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        DisposeCancellationTokenSource();
    }

    public async Task ConvertAsync(
        IReadOnlyList<SfdFileEntry> entries,
        string outputDir,
        DispatcherQueue dispatcher,
        Button convertButton,
        Button cancelButton,
        ProgressBar conversionProgress)
    {
        if (entries.Count == 0 || string.IsNullOrEmpty(outputDir))
            return;

        // Only checked rows participate; unchecked ones are skipped entirely.
        var items = entries.Where(entry => entry.IsChecked).ToList();
        if (items.Count == 0)
        {
            MainWindow.Instance?.SetStatus("No files checked for conversion.");
            return;
        }

        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        foreach (var entry in items)
            entry.Status = ExtractionStatus.Pending;

        convertButton.Visibility = Visibility.Collapsed;
        cancelButton.Visibility = Visibility.Visible;
        conversionProgress.Visibility = Visibility.Visible;
        conversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalConverted = 0;
        var totalFiles = items.Count;
        var token = cts.Token;

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in items)
                {
                    if (token.IsCancellationRequested)
                        break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var result = VideoConverterTabOperations.ConvertFromSource(
                        entry,
                        outputDir,
                        new Progress<double>(progress =>
                            dispatcher.TryEnqueue(() => entry.ConvertProgress = progress * 100)),
                        token);

                    var processed = Interlocked.Increment(ref filesProcessed);
                    if (result.Success)
                        Interlocked.Increment(ref totalConverted);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = result.Success ? ExtractionStatus.Done : ExtractionStatus.Error;
                        conversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
            }, token);

            stopwatch.Stop();
            conversionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Converted {totalConverted}/{totalFiles} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Conversion cancelled");
        }
        finally
        {
            DisposeCancellationTokenSource();
            cancelButton.Visibility = Visibility.Collapsed;
            convertButton.Visibility = Visibility.Visible;
        }
    }

    public async Task CancelAsync(Button convertButton, Button cancelButton)
    {
        var cts = _cts;
        if (cts != null)
        {
            _cts = null;
            await cts.CancelAsync();
            cts.Dispose();
        }

        cancelButton.Visibility = Visibility.Collapsed;
        convertButton.Visibility = Visibility.Visible;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    private void DisposeCancellationTokenSource()
    {
        _cts?.Dispose();
        _cts = null;
    }
}
