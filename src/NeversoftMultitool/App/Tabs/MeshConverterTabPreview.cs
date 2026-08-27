using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Detection;
using NeversoftMultitool.Core.Formats.Mesh.N64;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Rendering;
using Windows.ApplicationModel.DataTransfer;

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
    private string? _previewSourcePath;

    public MeshConverterTabPreview(ModelViewerControl viewer)
    {
        _viewer = viewer;
        _viewer.ViewPoseCopied += OnViewPoseCopied;
    }

    public void Dispose()
    {
        _viewer.ViewPoseCopied -= OnViewPoseCopied;
        var cts = Interlocked.Exchange(ref _previewCts, null);
        if (cts == null) return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>
    ///     P in the viewer: put the current viewpoint on the clipboard as arguments
    ///     the headless renderer can replay.
    /// </summary>
    /// <remarks>
    ///     The source path is prepended as a comment because a viewpoint means nothing
    ///     without the file it belongs to, and the viewer itself only ever sees GLB bytes.
    /// </remarks>
    private void OnViewPoseCopied(object? sender, CapturedView view)
    {
        var arguments = view.ToArguments();
        var text = _previewSourcePath is { Length: > 0 } source
            ? $"# {source}{Environment.NewLine}{arguments}"
            : arguments;

        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);

        MainWindow.Instance?.SetStatus($"View copied: {arguments}");
    }

    public Task InitializeAsync()
    {
        return _viewer.InitializeAsync();
    }

    /// <summary>
    ///     Level-scale content selects the viewer's default camera mode (levels
    ///     start in Fly, everything else in Orbit) and walk-height tuning. The
    ///     rule itself lives in <see cref="MeshLevelPolicy" /> so it is testable.
    /// </summary>
    internal static bool IsLevelModel(MeshFileEntry entry) => entry.IsLevelContent;

    public async Task<IReadOnlyList<ModelVisibilityGroup>?> LoadPreviewAsync(
        MeshFileEntry entry,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        bool preserveCamera = false,
        bool includeLevelObjects = true,
        XbxSkeletonSelection? xbxSkeletonSelection = null)
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
                    includeLevelObjects: includeLevelObjects,
                    preparedSkeleton: xbxSkeletonSelection?.Skeleton), token);

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
            _previewSourcePath = entry.FilePath;
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
        bool preserveCamera = false,
        SkaAnimationSourceRig? animationSourceRig = null,
        bool oneShot = false)
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
                    character, [animation], visibilityOverrides, animationSourceRig, oneShot),
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
            _previewSourcePath = character.FilePath;
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

    private static double? ResolveWalkEyeHeight(MeshFileEntry entry, bool isLevel) =>
        MeshLevelPolicy.ResolveWalkEyeHeight(entry.LevelFacts, isLevel);

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
