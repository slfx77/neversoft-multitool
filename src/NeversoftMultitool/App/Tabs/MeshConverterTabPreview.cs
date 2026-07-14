namespace NeversoftMultitool;

/// <summary>
///     Handles 3D model preview for MeshConverterTab. Converts selected mesh
///     files to GLB on a background thread, then loads the result into the
///     shared <see cref="ModelViewerControl" /> (WebView2 + model-viewer).
/// </summary>
internal sealed class MeshConverterTabPreview : IDisposable
{
    private readonly ModelViewerControl _viewer;
    private CancellationTokenSource? _previewCts;

    public MeshConverterTabPreview(ModelViewerControl viewer)
    {
        _viewer = viewer;
    }

    public void Dispose()
    {
        _previewCts?.Dispose();
        _previewCts = null;
    }

    public Task InitializeAsync()
    {
        return _viewer.InitializeAsync();
    }

    public async Task LoadPreviewAsync(MeshFileEntry entry)
    {
        // Cancel any in-flight preview
        var previousCts = _previewCts;
        if (previousCts != null)
        {
            _previewCts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        _viewer.SetError(null);
        _viewer.SetInfo($"Converting {entry.FileName}...");
        _viewer.SetLoading(true);
        await _viewer.SetViewerStatusAsync("Converting...");

        try
        {
            var (glbBytes, triangles) = await Task.Run(() =>
                MeshConverterTabFileConverter.ConvertToGlbBytes(entry), token);

            if (token.IsCancellationRequested) return;

            if (glbBytes == null || glbBytes.Length == 0)
            {
                // Clear the previous model so render buttons can't act on
                // stale bytes under this file's name.
                await _viewer.ClearAsync();
                _viewer.SetInfo("No geometry in this file");
                await _viewer.SetViewerStatusAsync("No geometry");
                return;
            }

            _viewer.SetInfo(
                $"{entry.FormatDisplay} | {triangles:N0} triangles | {glbBytes.Length / 1024:N0} KB");
            _viewer.SetLoading(false);
            await _viewer.LoadGlbAsync(glbBytes);
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;

            await _viewer.ClearAsync();
            _viewer.SetError($"Preview failed: {ex.Message}");
            await _viewer.SetViewerStatusAsync($"Error: {ex.Message}");
        }
    }

    public async Task ClearAsync()
    {
        var cts = _previewCts;
        if (cts != null)
        {
            _previewCts = null;
            await cts.CancelAsync();
            cts.Dispose();
        }

        await _viewer.ClearAsync();
    }
}
