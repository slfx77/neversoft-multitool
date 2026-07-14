using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool;

public sealed partial class MeshConverterTab : UserControl, IDisposable
{
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private CancellationTokenSource? _cts;
    private string _inputDir = "";
    private string _outputDir = "";
    private bool _outputManuallySet;
    private MeshConverterTabPreview? _preview;
    private CancellationTokenSource? _scanCts;

    public MeshConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        ModelViewer.ModelLoaded += ModelViewer_ModelLoaded;
        Unloaded += MeshConverterTab_Unloaded;
    }

    public void Dispose()
    {
        Unloaded -= MeshConverterTab_Unloaded;
        ModelViewer.ModelLoaded -= ModelViewer_ModelLoaded;
        _cts?.Dispose();
        _cts = null;
        _scanCts?.Dispose();
        _scanCts = null;
        _preview?.Dispose();
        _preview = null;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;
        DefaultOutputToInput(path);
        await RunRecursiveScan(path);
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(
            [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"]);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);

        await CancelInFlightScan();

        _items.Clear();
        ConvertButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        try
        {
            MainWindow.Instance?.SetStatus($"Scanning {Path.GetFileName(path)}...");

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanArchive(path, token),
                token);

            token.ThrowIfCancellationRequested();

            foreach (var entry in entries)
                _items.Add(entry);

            if (entries.Count == 0)
                MainWindow.Instance?.SetStatus(
                    $"{Path.GetFileName(path)}: no supported mesh entries found.");
            else
                MainWindow.Instance?.SetStatus(
                    $"Found {entries.Count} mesh entrie(s) in {Path.GetFileName(path)}.");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Scan cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    private async Task RunRecursiveScan(string rootDir)
    {
        await CancelInFlightScan();

        _items.Clear();
        ConvertButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;
        var dispatcher = DispatcherQueue;

        MainWindow.Instance?.SetStatus("Scanning directory...");

        try
        {
            var scanSummary = await Task.Run(
                () => MeshConverterTabFileScanner.AnalyzeDirectory(rootDir, token),
                token);

            if (scanSummary.UnsupportedFiles.Count > 0)
            {
                var proceed = await ScanSummaryDialog.ShowIfNeeded(
                    XamlRoot,
                    scanSummary.SupportedCount,
                    [.. scanSummary.UnsupportedFiles]);
                if (!proceed)
                {
                    MainWindow.Instance?.SetStatus("Scan cancelled");
                    return;
                }
            }

            var progress = new Progress<int>(count =>
                MainWindow.Instance?.SetStatus($"Scanning: {count} files probed..."));

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanDirectory(rootDir, progress, token),
                token);

            token.ThrowIfCancellationRequested();

            foreach (var entry in entries)
                _items.Add(entry);

            MainWindow.Instance?.SetStatus($"Found {entries.Count} mesh file(s).");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Scan cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    private async Task CancelInFlightScan()
    {
        var existing = _scanCts;
        if (existing == null) return;
        _scanCts = null;
        try
        {
            await existing.CancelAsync();
        }
        catch
        {
            // swallow
        }

        existing.Dispose();
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

    private void DefaultOutputToInput(string dir)
    {
        if (_outputManuallySet)
            return;

        _outputDir = dir;
        OutputPathText.Text = dir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);
        var hasFormat = ExportGlbCheckbox.IsChecked == true || ExportBlendCheckbox.IsChecked == true;

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput && hasFormat;
    }

    private void ExportFormatCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before later-declared elements exist.
        if (ConvertButton == null) return;
        UpdateUiState();
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        if (!TryGetWorldzoneScale(out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
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

        foreach (var file in _items)
        {
            file.TriangleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.Visibility = Visibility.Collapsed;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalTriangles = 0;
        var totalConverted = 0;
        var totalFiles = _items.Count;
        string? firstError = null;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;
        var token = cts.Token;
        var entries = _items.ToList();
        var worldzoneTimeOfDay = GetSelectedWorldzoneTimeOfDay();
        var outputFormat = GetSelectedOutputFormat();

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested)
                    break;

                dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                try
                {
                    var result = MeshConverterTabFileConverter.ConvertFile(
                        entry,
                        outputDir,
                        worldzoneTimeOfDay,
                        worldzoneScale,
                        outputFormat,
                        cancellationToken: token);
                    Interlocked.Add(ref totalTriangles, result.Triangles);
                    Interlocked.Increment(ref totalConverted);

                    var processed = Interlocked.Increment(ref filesProcessed);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TriangleCount = result.Triangles;
                        entry.Status = ExtractionStatus.Done;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                    var processed = Interlocked.Increment(ref filesProcessed);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = ExtractionStatus.Error;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        ConversionProgress.Value = 100;
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.Visibility = Visibility.Visible;
        var status = $"Converted {totalConverted}/{totalFiles} files " +
                     $"({totalTriangles:N0} triangles) in {stopwatch.Elapsed.TotalSeconds:F2}s";
        if (firstError != null)
            status += $". First error: {firstError}";
        MainWindow.Instance?.SetStatus(status);
    }

    private MeshOutputFormat GetSelectedOutputFormat()
    {
        var glb = ExportGlbCheckbox.IsChecked == true;
        var blend = ExportBlendCheckbox.IsChecked == true;
        if (glb && blend) return MeshOutputFormat.Both;
        return blend ? MeshOutputFormat.Blend : MeshOutputFormat.Glb;
    }

    private WorldzoneTimeOfDay GetSelectedWorldzoneTimeOfDay()
    {
        var tag = (WorldzoneTimeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag switch
        {
            "day" => WorldzoneTimeOfDay.Day,
            "night" => WorldzoneTimeOfDay.Night,
            _ => WorldzoneTimeOfDay.All
        };
    }

    private bool TryGetWorldzoneScale(out float scale)
    {
        var text = WorldzoneScaleText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            scale = 1f;
            return true;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
               && float.IsFinite(scale)
               && scale > 0f;
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
        ConvertButton.Visibility = Visibility.Visible;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderPngButton.IsEnabled = false;
        RenderGifButton.IsEnabled = false;

        if (FilesListView.SelectedItem is not MeshFileEntry entry)
        {
            if (_preview != null)
                await _preview.ClearAsync();
            return;
        }

        // Lazy-init the preview helper
        _preview ??= new MeshConverterTabPreview(ModelViewer);

        await _preview.InitializeAsync();
        await _preview.LoadPreviewAsync(entry);
        UpdateRenderButtons();
    }

    // ─── Render section (glb-render / glb-gif in-app) ─────────────────────

    private void ModelViewer_ModelLoaded(object? sender, EventArgs e)
    {
        UpdateRenderButtons();
    }

    private void UpdateRenderButtons()
    {
        var hasGlb = ModelViewer.LastGlbBytes != null;
        RenderPngButton.IsEnabled = hasGlb;
        RenderGifButton.IsEnabled = hasGlb && ModelViewer.HasAnimations;
    }

    private async void RenderPng_Click(object sender, RoutedEventArgs e)
    {
        var glbBytes = ModelViewer.LastGlbBytes;
        if (glbBytes == null) return;

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var stem = GetSelectedStem();
        var size = (int)RenderSizeBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;
        var useObjectReview = ObjectReviewCheckbox.IsChecked == true;

        RenderPngButton.IsEnabled = false;
        ConversionProgress.IsIndeterminate = true;
        ConversionProgress.Visibility = Visibility.Visible;

        try
        {
            var outputs = await Task.Run(() =>
            {
                var tempGlb = GlbScratchFile.Write(glbBytes, "MeshRender");
                try
                {
                    IReadOnlyList<RenderView> views = useObjectReview
                        ? GlbRenderPresets.ObjectReview
                        : [new RenderView("", azimuth, elevation)];

                    var written = new List<string>();
                    foreach (var view in views)
                    {
                        var suffix = view.Name.Length > 0 ? "_" + view.Name : "";
                        var pngPath = Path.Combine(outputDir, stem + suffix + ".png");
                        GlbRenderer.RenderToFile(tempGlb, pngPath, size, view.Azimuth, view.Elevation);
                        written.Add(pngPath);
                    }

                    return written;
                }
                finally
                {
                    GlbScratchFile.TryDelete(tempGlb);
                }
            });

            MainWindow.Instance?.SetStatus(outputs.Count == 1
                ? $"Rendered → {Path.GetFileName(outputs[0])}"
                : $"Rendered {outputs.Count} views → {outputDir}");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Render failed: {ex.Message}");
        }
        finally
        {
            ConversionProgress.IsIndeterminate = false;
            ConversionProgress.Visibility = Visibility.Collapsed;
            UpdateRenderButtons();
        }
    }

    private async void RenderGif_Click(object sender, RoutedEventArgs e)
    {
        var glbBytes = ModelViewer.LastGlbBytes;
        if (glbBytes == null) return;

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var stem = GetSelectedStem();
        var outputPath = Path.Combine(outputDir, stem + ".gif");
        var size = (int)RenderSizeBox.Value;
        var fps = (int)RenderFpsBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;

        RenderGifButton.IsEnabled = false;
        ConversionProgress.IsIndeterminate = true;
        ConversionProgress.Visibility = Visibility.Visible;

        try
        {
            var (frames, duration) = await Task.Run(() =>
            {
                var tempGlb = GlbScratchFile.Write(glbBytes, "MeshRender");
                try
                {
                    return GlbGifRenderer.RenderToFile(
                        tempGlb, outputPath, size, fps, azimuth, elevation);
                }
                finally
                {
                    GlbScratchFile.TryDelete(tempGlb);
                }
            });

            MainWindow.Instance?.SetStatus(
                $"Rendered {frames} frames ({duration:0.00}s) → {Path.GetFileName(outputPath)}");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"GIF render failed: {ex.Message}");
        }
        finally
        {
            ConversionProgress.IsIndeterminate = false;
            ConversionProgress.Visibility = Visibility.Collapsed;
            UpdateRenderButtons();
        }
    }

    private string GetSelectedStem()
    {
        return FilesListView.SelectedItem is MeshFileEntry entry
            ? MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName)
            : "render";
    }

    private void MeshConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
