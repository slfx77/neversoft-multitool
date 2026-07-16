using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh;

namespace NeversoftMultitool;

/// <summary>
///     Handles 3D model preview for the merged mesh/character tab. Converts
///     selected mesh files (or character + animation pairs) to GLB on a
///     background thread, then loads the result into the shared
///     <see cref="ModelViewerControl" /> (WebView2 + three.js). Both preview
///     flavors share ONE cancellation slot so a static preview and an animated
///     preview can never race each other into the viewer.
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

    /// <summary>
    ///     Level-scale content starts the viewer in Fly mode (F toggles Walk);
    ///     props/characters keep the Orbit default. Worldzones, RW BSP worlds,
    ///     scene files, DDM levels, and _g.psx level geometry qualify.
    /// </summary>
    internal static bool IsLevelModel(MeshFileEntry entry)
    {
        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone)
            return true;

        var name = entry.FileName;
        if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.ngc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase);
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

            // Surface the count in the file list too — before this, the Triangles
            // column stayed blank until a full Convert run.
            entry.TriangleCount = triangles;

            _viewer.SetInfo(
                $"{entry.FormatDisplay} | {triangles:N0} triangles | {glbBytes.Length / 1024:N0} KB");
            _viewer.SetLoading(false);
            await _viewer.LoadGlbAsync(glbBytes, IsLevelModel(entry));
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

    /// <summary>
    ///     Build a single-animation preview GLB for a character and push it
    ///     into the viewer (ported from the Character Preview tab).
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
