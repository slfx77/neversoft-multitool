using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool;

/// <summary>
///     Owns preview builds for the Character Preview tab. Builds
///     single-animation GLBs on demand and loads them into the shared
///     <see cref="ModelViewerControl" />. Mirrors the cancellation pattern
///     used by <see cref="MeshConverterTabPreview" />.
/// </summary>
internal sealed class CharacterPreviewTabPreview : IDisposable
{
    private readonly ModelViewerControl _viewer;
    private CancellationTokenSource? _previewCts;

    public CharacterPreviewTabPreview(ModelViewerControl viewer)
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

    /// <summary>
    ///     Build a single-animation preview GLB and push it into the viewer.
    ///     Cancels any in-flight preview build first.
    /// </summary>
    public async Task LoadPreviewAsync(MeshFileEntry character, AnimationProbe animation)
    {
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
        _viewer.SetInfo($"Building preview for {animation.DisplayName}…");
        _viewer.SetLoading(true);
        await _viewer.SetViewerStatusAsync("Building preview...");

        try
        {
            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(character, [animation]),
                token);

            if (token.IsCancellationRequested) return;

            if (result.GlbBytes == null || result.Triangles == 0)
            {
                // Clear the previous model so "Render GIF..." can't act on
                // stale bytes for a failed selection.
                await _viewer.ClearAsync();
                _viewer.SetError(result.Error ?? "Preview build returned no geometry.");
                await _viewer.SetViewerStatusAsync("No preview");
                return;
            }

            _viewer.SetInfo(
                $"{character.FormatDisplay} | {animation.DisplayName} | "
                + $"{animation.DurationSec:0.00} s | {result.Triangles:N0} triangles");
            _viewer.SetLoading(false);
            await _viewer.LoadGlbAsync(result.GlbBytes);
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;

            await _viewer.ClearAsync();
            _viewer.SetError($"Preview failed: {ex.Message}");
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
