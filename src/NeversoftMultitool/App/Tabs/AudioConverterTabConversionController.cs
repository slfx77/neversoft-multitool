using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool;

internal sealed class AudioConverterTabConversionController : IDisposable
{
    private CancellationTokenSource? _cts;

    public void Dispose()
    {
        DisposeCancellationTokenSource();
    }

    public async Task ConvertAsync(
        IReadOnlyList<AudioFileEntry> parentFiles,
        string outputDir,
        int vabSampleRate,
        DispatcherQueue dispatcher,
        Button convertButton,
        Button cancelButton,
        ProgressBar conversionProgress)
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
            file.SampleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        convertButton.Visibility = Visibility.Collapsed;
        cancelButton.Visibility = Visibility.Visible;
        conversionProgress.Visibility = Visibility.Visible;
        conversionProgress.Value = 0;

        using var scope = GlobalProgress.Begin("Converting audio files");

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = parentFiles.Count;
        var token = cts.Token;
        var entries = parentFiles.ToList();
        var outputStems = AudioOutputStemPlanner.Plan(entries
            .Select(static entry => new AudioOutputStemInput(entry.FileName, entry.RelativePath))
            .ToList());

        try
        {
            await Task.Run(() =>
            {
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    if (token.IsCancellationRequested)
                        break;

                    var entry = entries[entryIndex];
                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    try
                    {
                        var result = AudioConverterTabOperations.ConvertFile(
                            entry,
                            outputDir,
                            vabSampleRate,
                            outputStems[entryIndex]);

                        var processed = Interlocked.Increment(ref filesProcessed);
                        scope.Report(processed, totalFiles);
                        dispatcher.TryEnqueue(() =>
                        {
                            entry.SampleCount = result.SamplesWritten;
                            entry.Status = result.Success ? ExtractionStatus.Done : ExtractionStatus.Error;
                            conversionProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                    catch
                    {
                        var processed = Interlocked.Increment(ref filesProcessed);
                        scope.Report(processed, totalFiles);
                        dispatcher.TryEnqueue(() =>
                        {
                            entry.Status = ExtractionStatus.Error;
                            conversionProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                }
            }, token);

            stopwatch.Stop();
            conversionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Converted {filesProcessed} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
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
