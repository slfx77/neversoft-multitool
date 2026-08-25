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
    // Explicit player-scale hints keep walk height independent of a level's
    // bounding sphere. SM2:EE's player is 90.861 units tall, so 82 remains the
    // observed-correct eye. THAW's skater is 73 units tall, yielding 66. The
    // older Apocalypse v3 level space needs the lower empirically requested
    // height while retaining a stable header-based classification.
    //
    // THPS settled on the bench anthropometry after two rounds of user
    // feedback: 58 and then 70 both read as too short beside skny's park
    // benches (eye level with the backrest). The authored bench legs run
    // ground-to-seat over 28.444 units ≈ 0.45 m, giving ~63 units/m, so a
    // 1.6 m eye sits near 100. The old "giant at 82" report (THPS2 DC) is
    // superseded by the two direct bench comparisons — trust the furniture.
    // A platform-scale explanation was ruled out by measurement: THPS2 PSX
    // and DC skny have identical extents (21655.6 x 5059.1 x 32914.2) and
    // identical 28.444-unit bench legs.
    private const double PsxLevelWalkEyeHeight = 82d;
    private const double ApocalypseLevelWalkEyeHeight = 56d;
    private const double ThpsLevelWalkEyeHeight = 100d;
    private const double ThawWorldzoneWalkEyeHeight = 66d;

    // GBA levels export at GbaLevelGeometryWriter.Scale (16 GLB units per world
    // unit); a skater's eye at ~1.4 world units gives 22.
    private const double GbaLevelWalkEyeHeight = 22d;

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
    ///     Identifies level-scale content for the viewer's default camera mode
    ///     (levels start in Fly, everything else in Orbit) and walk-height
    ///     tuning. Worldzones, Apocalypse level files, RW BSP worlds, scene
    ///     files, placed DDM levels, _g.psx level geometry, and PSX levels the
    ///     companion resolver recognizes (THPS bare-stem levels with sibling
    ///     _o.psx+_t.trg, Apocalypse chunk primaries) qualify.
    /// </summary>
    internal static bool IsLevelModel(MeshFileEntry entry)
    {
        // Carved N64 bundles: bounds.bin's largest per-mesh radius separates
        // world content (level geometry AND level object banks, both authored in
        // world space) from characters and props, with an empty band between the
        // two classes. Precision 1.000 over 328 PS1-Rosetta-labelled bundles.
        if (entry.IsN64Model)
            return N64BundleClassifier.IsWorldScale(entry.N64MaxBoundsRadius);

        if (entry.Ps2SubFormat == Ps2SceneSubFormat.PakWorldzone)
            return true;

        // Supers are animated characters by definition (the anim-chunk flag),
        // never levels — Apocalypse's war/thebeast/bruce are v3 supers.
        if (entry.IsPsx && !entry.PsxIsSuperModel &&
            entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return true;

        // THPS1/THPS2 bare-stem levels carry no _g suffix; the scanner's
        // corpus-proven level-companion resolution is the reliable signal.
        if (entry.IsPsx && entry.HasSupportedLevelObjectCompanion)
            return true;

        var name = entry.FileName;
        if (name.EndsWith(".bsp", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.xbx", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.wpc", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".scn.ngc", StringComparison.OrdinalIgnoreCase) ||
            // Carved GBA level records are levels by definition.
            name.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Only PLACED DDMs are levels; a standalone DDM with no PSX layout
        // companion is a character/prop model (THPS2X skaters, items).
        if (name.EndsWith(".ddm", StringComparison.OrdinalIgnoreCase))
            return entry.HasPlacedPsxCompanion;

        // THPS4/THUG whole-level geoms ship under Levels/<Stem>/ (both on
        // disc and inside the scene PREs); prop/vehicle geoms live elsewhere.
        if (entry.IsPs2Geom && PathContainsLevelsSegment(entry))
            return true;

        return name.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathContainsLevelsSegment(MeshFileEntry entry)
    {
        return ContainsLevelsSegment(entry.RelativePath) || ContainsLevelsSegment(entry.FilePath);

        static bool ContainsLevelsSegment(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var parts = path.Split(['/', '\\'], StringSplitOptions.None);
            // The last part is the file name; only directory segments count.
            for (var i = 0; i < parts.Length - 1; i++)
            {
                if (string.Equals(parts[i], "Levels", StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }

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

    private static double? ResolveWalkEyeHeight(MeshFileEntry entry, bool isLevel)
    {
        if (!isLevel) return null;
        if (entry.IsPakWorldzone) return ThawWorldzoneWalkEyeHeight;
        if (entry.FileName.EndsWith(MeshTypeDetector.GbaLevelSuffix, StringComparison.OrdinalIgnoreCase))
            return GbaLevelWalkEyeHeight;

        // N64 bundles are emitted at k / ScaleDivisor with k = 1 for non-supers,
        // which IS the PS1 translation divisor — the same level exports at the
        // same world scale on both platforms, so the PS1 eye heights transfer
        // unchanged. Only level GEOMETRY gets one: an object bank flies but has
        // no floor to stand on. An unnamed bundle falls back to the THPS eye.
        if (entry.IsN64Model)
        {
            if (!N64BundleClassifier.IsLevel(entry.N64MaxBoundsRadius, entry.ObjectCount))
                return null;

            return entry.FileName.EndsWith("_g.psx.n64", StringComparison.OrdinalIgnoreCase)
                ? PsxLevelWalkEyeHeight
                : ThpsLevelWalkEyeHeight;
        }

        if (!entry.IsPsx) return null;
        if (entry.PsxFormatRevision == PsxMeshFormatRevision.ApocalypseV3)
            return ApocalypseLevelWalkEyeHeight;

        // THPS1/THPS2 levels are bare-stem (no _g suffix) and recognized via
        // their level-object companions; Spider-Man/SM2:EE levels keep the
        // taller superhero eye.
        if (entry.HasSupportedLevelObjectCompanion &&
            !entry.FileName.EndsWith("_g.psx", StringComparison.OrdinalIgnoreCase))
        {
            return ThpsLevelWalkEyeHeight;
        }

        return PsxLevelWalkEyeHeight;
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
