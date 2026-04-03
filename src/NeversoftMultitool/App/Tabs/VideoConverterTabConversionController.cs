using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class VideoConverterTabConversionController : IDisposable
{
    private CancellationTokenSource? _cts;

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

        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _cts = cts;

        foreach (var entry in entries)
            entry.Status = ExtractionStatus.Pending;

        convertButton.IsEnabled = false;
        cancelButton.Visibility = Visibility.Visible;
        conversionProgress.Visibility = Visibility.Visible;
        conversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalConverted = 0;
        var totalFiles = entries.Count;
        var token = cts.Token;
        var items = entries.ToList();

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in items)
                {
                    if (token.IsCancellationRequested)
                        break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var result = VideoConverterTabOperations.ConvertFile(
                        entry.FilePath,
                        outputDir,
                        new Progress<double>(progress => dispatcher.TryEnqueue(() => entry.ConvertProgress = progress * 100)),
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
            convertButton.IsEnabled = true;
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
        convertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
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
}
