using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool;

/// <summary>
///     Sequential batch operations over the merged tab's CHECKED file entries:
///     Convert (the loop that used to live in ConvertButton_Click), batch PNG
///     renders, and batch GIF renders (headless rasterizer, skipping GLBs with
///     no animation clips). Owns the Convert↔Cancel swap and the progress bar.
/// </summary>
internal sealed class MeshConverterTabBatchRunner(
    ProgressBar progressBar,
    Button convertButton,
    Button cancelButton,
    DispatcherQueue dispatcher) : IDisposable
{
    private CancellationTokenSource? _cts;
    private IGlobalProgressScope? _progressScope;

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
        _progressScope?.Dispose();
        _progressScope = null;
    }

    public async Task CancelAsync()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        await cts.CancelAsync();
        cts.Dispose();
    }

    public async Task ConvertAsync(
        IReadOnlyList<MeshFileEntry> entries,
        string outputDir,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        float worldzoneScale,
        MeshOutputFormat outputFormat,
        string? singleOutputStem = null,
        MeshFileEntry? visibilityEntry = null,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true)
    {
        var skeletons = CaptureSkeletons(entries);
        var cts = await BeginOperationAsync("Converting meshes");
        var scope = _progressScope;

        foreach (var file in entries)
        {
            file.TriangleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalTriangles = 0;
        var totalConverted = 0;
        var totalFiles = entries.Count;
        string? firstError = null;
        var token = cts.Token;

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
                        outputStem: entries.Count == 1 ? singleOutputStem : null,
                        visibilityOverrides: ReferenceEquals(entry, visibilityEntry)
                            ? visibilityOverrides
                            : null,
                        includeLevelObjects: includeLevelObjects,
                        preparedSkeleton: skeletons[entry],
                        cancellationToken: token);
                    Interlocked.Add(ref totalTriangles, result.Triangles);
                    Interlocked.Increment(ref totalConverted);

                    var processed = Interlocked.Increment(ref filesProcessed);
                    scope?.Report(processed, totalFiles);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TriangleCount = result.Triangles;
                        entry.Status = ExtractionStatus.Done;
                        progressBar.Value = (double)processed / totalFiles * 100;
                    });
                }
                catch (Exception ex)
                {
                    firstError ??= ex.Message;
                    var processed = Interlocked.Increment(ref filesProcessed);
                    scope?.Report(processed, totalFiles);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = ExtractionStatus.Error;
                        progressBar.Value = (double)processed / totalFiles * 100;
                    });
                }
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        EndOperation(cts);
        progressBar.Value = 100;

        var status = $"Converted {totalConverted}/{totalFiles} files " +
                     $"({totalTriangles:N0} triangles) in {stopwatch.Elapsed.TotalSeconds:F2}s";
        if (firstError != null)
            status += $". First error: {firstError}";
        MainWindow.Instance?.SetStatus(status);
    }

    /// <summary>
    ///     Renders each checked entry to PNG(s). With one entry checked and a
    ///     preview loaded, the caller passes the preview GLB directly instead.
    /// </summary>
    public async Task RenderPngBatchAsync(
        IReadOnlyList<MeshFileEntry> entries,
        string outputDir,
        int size,
        float azimuth,
        float elevation,
        bool objectReview,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        float worldzoneScale,
        MeshFileEntry? visibilityEntry = null,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true)
    {
        var skeletons = CaptureSkeletons(entries);
        var cts = await BeginOperationAsync("Rendering PNGs");
        var scope = _progressScope;
        var token = cts.Token;
        var rendered = 0;
        var skipped = 0;
        string? firstError = null;

        await Task.Run(() =>
        {
            var processed = 0;
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var (glb, _) = MeshConverterTabFileConverter.ConvertToGlbBytes(
                        entry,
                        worldzoneTimeOfDay,
                        worldzoneScale,
                        ReferenceEquals(entry, visibilityEntry) ? visibilityOverrides : null,
                        includeLevelObjects,
                        skeletons[entry]);
                    if (glb == null || glb.Length == 0)
                    {
                        skipped++;
                    }
                    else
                    {
                        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
                        RenderGlbToPngs(glb, outputDir, stem, size, azimuth, elevation, objectReview);
                        rendered++;
                    }
                }
                catch (Exception ex)
                {
                    firstError ??= $"{entry.FileName}: {ex.Message}";
                    skipped++;
                }

                processed++;
                scope?.Report(processed, entries.Count);
                var progress = (double)processed / entries.Count * 100;
                dispatcher.TryEnqueue(() => progressBar.Value = progress);
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        EndOperation(cts);
        var status = $"Rendered {rendered} model(s) → {outputDir}";
        if (skipped > 0) status += $", {skipped} skipped";
        if (firstError != null) status += $". First error: {firstError}";
        MainWindow.Instance?.SetStatus(status);
    }

    /// <summary>Renders one already-built GLB (the loaded preview) to PNG(s).</summary>
    public async Task RenderPngSingleAsync(
        byte[] glbBytes,
        string outputDir,
        string stem,
        int size,
        float azimuth,
        float elevation,
        bool objectReview)
    {
        var cts = await BeginOperationAsync("Rendering PNG", indeterminate: true);
        try
        {
            var outputs = await Task.Run(
                () => RenderGlbToPngs(glbBytes, outputDir, stem, size, azimuth, elevation, objectReview),
                cts.Token);

            MainWindow.Instance?.SetStatus(outputs.Count == 1
                ? $"Rendered → {Path.GetFileName(outputs[0])}"
                : $"Rendered {outputs.Count} views → {outputDir}");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Render cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Render failed: {ex.Message}");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    ///     Converts and renders one entry. This is used when a worldzone must be
    ///     rebuilt with export-only lighting/scale settings or when no preview
    ///     GLB has been loaded yet.
    /// </summary>
    public async Task RenderPngEntryAsync(
        MeshFileEntry entry,
        string outputDir,
        string stem,
        int size,
        float azimuth,
        float elevation,
        bool objectReview,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        float worldzoneScale,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true)
    {
        var preparedSkeleton = entry.XbxSkeletonSelection?.Skeleton;
        var cts = await BeginOperationAsync("Rendering PNG", indeterminate: true);
        try
        {
            var outputs = await Task.Run(() =>
            {
                var (glb, _) = MeshConverterTabFileConverter.ConvertToGlbBytes(
                    entry,
                    worldzoneTimeOfDay,
                    worldzoneScale,
                    visibilityOverrides,
                    includeLevelObjects,
                    preparedSkeleton);
                if (glb == null || glb.Length == 0)
                    throw new InvalidOperationException("The selected mesh produced no geometry.");

                return RenderGlbToPngs(
                    glb, outputDir, stem, size, azimuth, elevation, objectReview);
            }, cts.Token);

            MainWindow.Instance?.SetStatus(outputs.Count == 1
                ? $"Rendered → {Path.GetFileName(outputs[0])}"
                : $"Rendered {outputs.Count} views → {outputDir}");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Render cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Render failed: {ex.Message}");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    ///     Renders each checked entry to an animated GIF; entries whose GLB has
    ///     no animation clips are skipped (plain meshes convert clip-less).
    /// </summary>
    public async Task RenderGifBatchAsync(
        IReadOnlyList<MeshFileEntry> entries,
        string outputDir,
        int size,
        int fps,
        float azimuth,
        float elevation,
        WorldzoneTimeOfDay worldzoneTimeOfDay,
        float worldzoneScale,
        MeshFileEntry? visibilityEntry = null,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool includeLevelObjects = true)
    {
        var skeletons = CaptureSkeletons(entries);
        var cts = await BeginOperationAsync("Rendering GIFs");
        var scope = _progressScope;
        var token = cts.Token;
        var rendered = 0;
        var skipped = 0;
        string? firstError = null;

        await Task.Run(() =>
        {
            var processed = 0;
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested) break;

                try
                {
                    var (glb, _) = MeshConverterTabFileConverter.ConvertToGlbBytes(
                        entry,
                        worldzoneTimeOfDay,
                        worldzoneScale,
                        ReferenceEquals(entry, visibilityEntry) ? visibilityOverrides : null,
                        includeLevelObjects,
                        skeletons[entry]);
                    if (glb == null || glb.Length == 0 || !GlbHasAnimations(glb))
                    {
                        skipped++;
                    }
                    else
                    {
                        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
                        var outputPath = Path.Combine(outputDir, stem + ".gif");
                        RenderGlbToGif(glb, outputPath, size, fps, azimuth, elevation);
                        rendered++;
                    }
                }
                catch (Exception ex)
                {
                    firstError ??= $"{entry.FileName}: {ex.Message}";
                    skipped++;
                }

                processed++;
                scope?.Report(processed, entries.Count);
                var progress = (double)processed / entries.Count * 100;
                dispatcher.TryEnqueue(() => progressBar.Value = progress);
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        EndOperation(cts);
        var status = $"Rendered {rendered} GIF(s) → {outputDir}";
        if (skipped > 0) status += $", skipped {skipped} (no animation / no geometry)";
        if (firstError != null) status += $". First error: {firstError}";
        MainWindow.Instance?.SetStatus(status);
    }

    /// <summary>Renders one already-built GLB (the loaded preview) to a GIF.</summary>
    public async Task RenderGifSingleAsync(
        byte[] glbBytes,
        string outputPath,
        int size,
        int fps,
        float azimuth,
        float elevation)
    {
        var cts = await BeginOperationAsync("Rendering GIF", indeterminate: true);
        try
        {
            var (frames, duration) = await Task.Run(
                () => RenderGlbToGif(glbBytes, outputPath, size, fps, azimuth, elevation),
                cts.Token);

            MainWindow.Instance?.SetStatus(
                $"Rendered {frames} frames ({duration:0.00}s) → {Path.GetFileName(outputPath)}");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("GIF render cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"GIF render failed: {ex.Message}");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    private static List<string> RenderGlbToPngs(
        byte[] glbBytes,
        string outputDir,
        string stem,
        int size,
        float azimuth,
        float elevation,
        bool objectReview)
    {
        var tempGlb = GlbScratchFile.Write(glbBytes, "MeshRender");
        try
        {
            IReadOnlyList<RenderView> views = objectReview
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
    }

    private static (int Frames, double Duration) RenderGlbToGif(
        byte[] glbBytes,
        string outputPath,
        int size,
        int fps,
        float azimuth,
        float elevation)
    {
        var tempGlb = GlbScratchFile.Write(glbBytes, "MeshRender");
        try
        {
            return GlbGifRenderer.RenderToFile(tempGlb, outputPath, size, fps, azimuth, elevation);
        }
        finally
        {
            GlbScratchFile.TryDelete(tempGlb);
        }
    }

    private static bool GlbHasAnimations(byte[] glb)
    {
        try
        {
            if (glb.Length < 20) return false;
            var jsonLength = BitConverter.ToInt32(glb, 12);
            if (jsonLength <= 0 || 20 + jsonLength > glb.Length) return false;
            var json = Encoding.UTF8.GetString(glb, 20, jsonLength);
            using var doc = JsonDocument.Parse(json.TrimEnd('\0', ' '));
            return doc.RootElement.TryGetProperty("animations", out var anims) &&
                   anims.GetArrayLength() > 0;
        }
        catch
        {
            return false;
        }
    }

    private static IReadOnlyDictionary<MeshFileEntry, Ps2Skeleton?> CaptureSkeletons(
        IReadOnlyList<MeshFileEntry> entries) =>
        entries.ToDictionary(
            static entry => entry,
            static entry => entry.XbxSkeletonSelection?.Skeleton);

    private async Task<CancellationTokenSource> BeginOperationAsync(string label, bool indeterminate = false)
    {
        var previousCts = _cts;
        if (previousCts != null)
        {
            _cts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        _progressScope?.Dispose();
        _progressScope = GlobalProgress.Begin(label, indeterminate);

        var cts = new CancellationTokenSource();
        _cts = cts;

        convertButton.Visibility = Visibility.Collapsed;
        cancelButton.Visibility = Visibility.Visible;
        progressBar.Visibility = Visibility.Visible;
        progressBar.IsIndeterminate = false;
        progressBar.Value = 0;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        cancelButton.Visibility = Visibility.Collapsed;
        convertButton.Visibility = Visibility.Visible;
        progressBar.Visibility = Visibility.Collapsed;

        // A superseding BeginOperationAsync installs a new CTS before the old
        // operation's finally runs — its scope must survive this EndOperation.
        // (_cts == null means this op was cancelled: still ours to close.)
        if (_cts == null || ReferenceEquals(_cts, cts))
        {
            _progressScope?.Dispose();
            _progressScope = null;
        }

        if (ReferenceEquals(_cts, cts)) _cts = null;
        cts.Dispose();
    }
}
