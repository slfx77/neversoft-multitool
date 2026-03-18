using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class AudioConverterTabConversionController
{
    private CancellationTokenSource? _cts;

    public async Task ConvertAsync(
        IReadOnlyList<AudioFileEntry> parentFiles,
        string inputDir,
        string outputDir,
        int vabSampleRate,
        DispatcherQueue dispatcher,
        Button convertButton,
        Button cancelButton,
        ProgressBar conversionProgress)
    {
        if (parentFiles.Count == 0 || string.IsNullOrEmpty(outputDir))
            return;

        _cts = new CancellationTokenSource();

        foreach (var file in parentFiles)
        {
            file.SampleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        convertButton.IsEnabled = false;
        cancelButton.Visibility = Visibility.Visible;
        conversionProgress.Visibility = Visibility.Visible;
        conversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = parentFiles.Count;
        var token = _cts.Token;
        var entries = parentFiles.ToList();

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested)
                        break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);
                    var inputFile = Path.Combine(inputDir, entry.FileName);

                    try
                    {
                        var result = AudioConverterTabOperations.ConvertFile(
                            inputFile,
                            outputDir,
                            entry.AudioFormat,
                            vabSampleRate);

                        var processed = Interlocked.Increment(ref filesProcessed);
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
            cancelButton.Visibility = Visibility.Collapsed;
            convertButton.IsEnabled = true;
        }
    }

    public void Cancel(Button convertButton, Button cancelButton)
    {
        _cts?.Cancel();
        cancelButton.Visibility = Visibility.Collapsed;
        convertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }
}
