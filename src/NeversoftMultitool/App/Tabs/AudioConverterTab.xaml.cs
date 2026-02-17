using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Audio;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class AudioConverterTab : UserControl
{
    private static readonly string[] SupportedExtensions = [".adx", ".xa", ".vab", ".kat"];

    private readonly ObservableCollection<AudioFileEntry> _files = [];
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _cts;

    public AudioConverterTab()
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
        var audioFiles = Directory.GetFiles(_inputDir)
            .Where(f => SupportedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in audioFiles)
        {
            var fileName = Path.GetFileName(filePath)!;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            _files.Add(new AudioFileEntry
            {
                FileName = fileName,
                AudioFormat = DetectFormat(ext)
            });
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
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private int GetSelectedVabSampleRate()
    {
        var selected = (VabSampleRateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        return selected switch
        {
            "22050 Hz" => 22050,
            "44100 Hz" => 44100,
            _ => 11025
        };
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_files.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _files)
        {
            file.SampleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _files.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var inputDir = _inputDir;
        var outputDir = _outputDir;
        var vabSampleRate = GetSelectedVabSampleRate();

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in _files)
                {
                    if (token.IsCancellationRequested) break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var inputFile = Path.Combine(inputDir, entry.FileName);

                    try
                    {
                        var result = entry.AudioFormat switch
                        {
                            "ADX" => AdxDecoder.ConvertToWav(inputFile, outputDir),
                            "XA" => XaDecoder.ConvertToWav(inputFile, outputDir),
                            "VAB" => VabExtractor.ExtractToWav(inputFile, outputDir, vabSampleRate),
                            "KAT" => KatExtractor.ExtractToWav(inputFile, outputDir),
                            _ => new AudioConvertResult { ErrorMessage = "Unknown format" }
                        };

                        var processed = Interlocked.Increment(ref filesProcessed);

                        dispatcher.TryEnqueue(() =>
                        {
                            entry.SampleCount = result.SamplesWritten;
                            entry.Status = result.Success
                                ? ExtractionStatus.Done
                                : ExtractionStatus.Error;
                            ConversionProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                    catch
                    {
                        var processed = Interlocked.Increment(ref filesProcessed);
                        dispatcher.TryEnqueue(() =>
                        {
                            entry.Status = ExtractionStatus.Error;
                            ConversionProgress.Value = (double)processed / totalFiles * 100;
                        });
                    }
                }
            }, token);

            stopwatch.Stop();
            ConversionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Converted {filesProcessed} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Conversion cancelled");
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConvertButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    private static string DetectFormat(string extension) => extension switch
    {
        ".adx" => "ADX",
        ".xa" => "XA",
        ".vab" => "VAB",
        ".kat" => "KAT",
        _ => "Unknown"
    };
}
