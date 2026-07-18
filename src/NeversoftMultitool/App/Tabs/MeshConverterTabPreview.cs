using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

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
    // Explicit player-scale hints keep walk height independent of a level's
    // bounding sphere. SM2:EE's player is 90.861 units tall, so 82 remains the
    // observed-correct eye. THAW's skater is 73 units tall, yielding 66. The
    // older Apocalypse v3 level space needs the lower empirically requested
    // height while retaining a stable header-based classification.
    private const double PsxLevelWalkEyeHeight = 82d;
    private const double ApocalypseLevelWalkEyeHeight = 56d;
    private const double ThawWorldzoneWalkEyeHeight = 66d;

    private readonly ModelViewerControl _viewer;
    private CancellationTokenSource? _previewCts;

    public MeshConverterTabPreview(ModelViewerControl viewer)
    {
        _viewer = viewer;
    }

    public void Dispose()
    {
        var cts = Interlocked.Exchange(ref _previewCts, null);
        if (cts == null) return;
        cts.Cancel();
        cts.Dispose();
    }

    public Task InitializeAsync()
    {
        return _viewer.InitializeAsync();
    }

    /// <summary>
    ///     Identifies level-scale content for walk-height tuning. Worldzones,
    ///     Apocalypse level files, RW BSP worlds, scene files, DDM levels, and
    ///     _g.psx level geometry qualify.
    /// </summary>
    internal static bool IsLevelModel(MeshFileEntry entry)
    {
        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone)
            return true;

        if (entry.IsPsx && !entry.PsxIsSuperModel &&
            entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
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

    public async Task<IReadOnlyList<ModelVisibilityGroup>?> LoadPreviewAsync(
        MeshFileEntry entry,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool preserveCamera = false,
        bool includeLevelObjects = true)
    {
        var cts = await ReplacePreviewCancellationAsync();
        if (cts == null) return null;
        var token = cts.Token;

        await _viewer.CancelPendingLoadAsync();
        if (!IsCurrentPreview(cts)) return null;
        _viewer.SetError(null);
        _viewer.SetInfo($"Converting {entry.FileName}...");
        _viewer.SetLoading(true);
        await _viewer.SetViewerStatusAsync("Converting...");
        if (!IsCurrentPreview(cts)) return null;

        try
        {
            var (glbBytes, triangles, visibilityGroups) = await Task.Run(() =>
                MeshConverterTabFileConverter.ConvertToGlbPreview(
                    entry,
                    worldzoneTimeOfDay,
                    visibilityOverrides: visibilityOverrides,
                    includeLevelObjects: includeLevelObjects), token);

            if (token.IsCancellationRequested || !IsCurrentPreview(cts)) return null;

            if (glbBytes == null || glbBytes.Length == 0)
            {
                // Clear the previous model so render buttons can't act on
                // stale bytes under this file's name.
                await _viewer.ClearAsync();
                if (!IsCurrentPreview(cts)) return null;
                _viewer.SetInfo("No geometry in this file");
                await _viewer.SetViewerStatusAsync("No geometry");
                return visibilityGroups;
            }

            // Surface the count in the file list too — before this, the Triangles
            // column stayed blank until a full Convert run.
            entry.TriangleCount = triangles;

            _viewer.SetInfo(
                $"{entry.FormatDisplay} | {triangles:N0} triangles | {glbBytes.Length / 1024:N0} KB");
            _viewer.SetLoading(false);
            var isLevel = IsLevelModel(entry);
            var walkEyeHeight = ResolveWalkEyeHeight(entry, isLevel);
            await _viewer.LoadGlbAsync(
                glbBytes,
                isLevel,
                walkEyeHeight,
                preserveCamera);
            return IsCurrentPreview(cts) ? visibilityGroups : null;
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly
            return null;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !IsCurrentPreview(cts)) return null;

            await _viewer.ClearAsync();
            if (!IsCurrentPreview(cts)) return null;
            _viewer.SetError($"Preview failed: {ex.Message}");
            await _viewer.SetViewerStatusAsync($"Error: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Build a single-animation preview GLB for a character and push it
    ///     into the viewer (ported from the Character Preview tab).
    /// </summary>
    public async Task<IReadOnlyList<ModelVisibilityGroup>?> LoadPreviewAsync(
        MeshFileEntry character,
        AnimationProbe animation,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool preserveCamera = false)
    {
        var cts = await ReplacePreviewCancellationAsync();
        if (cts == null) return null;
        var token = cts.Token;

        await _viewer.CancelPendingLoadAsync();
        if (!IsCurrentPreview(cts)) return null;
        _viewer.SetError(null);
        _viewer.SetInfo($"Building preview for {animation.DisplayName}…");
        _viewer.SetLoading(true);
        await _viewer.SetViewerStatusAsync("Building preview...");
        if (!IsCurrentPreview(cts)) return null;

        try
        {
            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(
                    character, [animation], visibilityOverrides),
                token);

            if (token.IsCancellationRequested || !IsCurrentPreview(cts)) return null;

            if (result.GlbBytes == null || result.Triangles == 0)
            {
                // Clear the previous model so "Render GIF..." can't act on
                // stale bytes for a failed selection.
                await _viewer.ClearAsync();
                if (!IsCurrentPreview(cts)) return null;
                _viewer.SetError(result.Error ?? "Preview build returned no geometry.");
                await _viewer.SetViewerStatusAsync("No preview");
                return result.VisibilityGroups;
            }

            // Animated character selection takes this path instead of the
            // static preview path, so publish the already-computed count to
            // the shared file-table row here as well.
            character.TriangleCount = result.Triangles;
            _viewer.SetInfo(
                $"{character.FormatDisplay} | {animation.DisplayName} | "
                + $"{animation.DurationSec:0.00} s | {result.Triangles:N0} triangles");
            _viewer.SetLoading(false);
            await _viewer.LoadGlbAsync(
                result.GlbBytes,
                preserveCamera: preserveCamera);
            return IsCurrentPreview(cts) ? result.VisibilityGroups : null;
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly.
            return null;
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested || !IsCurrentPreview(cts)) return null;

            await _viewer.ClearAsync();
            if (!IsCurrentPreview(cts)) return null;
            _viewer.SetError($"Preview failed: {ex.Message}");
            return null;
        }
    }

    private static double? ResolveWalkEyeHeight(MeshFileEntry entry, bool isLevel)
    {
        if (!isLevel) return null;
        if (entry.IsPakWorldzone) return ThawWorldzoneWalkEyeHeight;
        if (!entry.IsPsx) return null;
        return entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3
            ? ApocalypseLevelWalkEyeHeight
            : PsxLevelWalkEyeHeight;
    }

    public async Task ClearAsync()
    {
        var cts = Interlocked.Exchange(ref _previewCts, null);
        if (cts != null)
        {
            await cts.CancelAsync();
            cts.Dispose();
        }

        // A newer selection may have started while cancellation callbacks ran.
        // In that case its preview owns the viewer and must not be cleared.
        if (Volatile.Read(ref _previewCts) != null) return;
        await _viewer.ClearAsync();
    }

    private async Task<CancellationTokenSource?> ReplacePreviewCancellationAsync()
    {
        // Publish the replacement before awaiting cancellation. Otherwise a
        // newer request can install its CTS during the await and then be
        // overwritten when this older request resumes.
        var replacement = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, replacement);
        if (previous != null)
        {
            await previous.CancelAsync();
            previous.Dispose();
        }

        return IsCurrentPreview(replacement) ? replacement : null;
    }

    private bool IsCurrentPreview(CancellationTokenSource cts) =>
        ReferenceEquals(Volatile.Read(ref _previewCts), cts) && !cts.IsCancellationRequested;
}
