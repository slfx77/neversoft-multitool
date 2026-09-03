using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;

namespace NeversoftMultitool;

/// <summary>
///     Animated GLB / Blend export for the merged tab's Animations pane
///     (ported from the Character Preview tab). Exports the checked,
///     skeleton-matching animations of the selected character. Shares the
///     tab's progress bar and Cancel button.
/// </summary>
internal sealed class MeshConverterTabAnimationExporter(
    ProgressBar progressBar,
    Button cancelButton) : IDisposable
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

    public void Cancel()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        cts.Dispose();
    }

    public async Task ExportGlbAsync(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        SkaAnimationSourceRig? animationSourceRig = null,
        bool oneShot = false)
    {
        if (animations.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one matching animation to export.");
            return;
        }

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var outputPath = await FilePickerHelper.PickSaveFileAsync(
            characterStem,
            ("glTF model", [".glb"]));
        if (outputPath == null) return;

        var cts = BeginOperation("Exporting animated GLB");
        try
        {
            // GBA clips animate by morphing, and a glTF weights track addresses
            // every target of the mesh — so each clip is its own file.
            if (character.IsGbaModel)
            {
                var written = await Task.Run(
                    () => ExportGbaClipFiles(
                        character, animations, outputPath, MeshOutputFormat.Glb, cts.Token),
                    cts.Token);
                MainWindow.Instance?.SetStatus(written > 0
                    ? $"Exported {written} clip(s) beside {Path.GetFileName(outputPath)}"
                    : "No GBA clips could be exported.");
                return;
            }

            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(
                    character, animations, visibilityOverrides, animationSourceRig, oneShot),
                cts.Token);

            if (result.GlbBytes == null)
            {
                MainWindow.Instance?.SetStatus(result.Error ?? "Convert failed.");
                return;
            }

            await File.WriteAllBytesAsync(outputPath, result.GlbBytes, cts.Token);
            MainWindow.Instance?.SetStatus(
                $"Exported {animations.Count} animation(s) → {Path.GetFileName(outputPath)}");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Export cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Export failed: {ex.Message}");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    public async Task ExportBlendAsync(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        IReadOnlyDictionary<string, bool>? visibilityOverrides = null,
        SkaAnimationSourceRig? animationSourceRig = null,
        bool oneShot = false)
    {
        if (animations.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one matching animation to export.");
            return;
        }

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var outputPath = await FilePickerHelper.PickSaveFileAsync(
            characterStem,
            ("Blender model", [".blend"]));
        if (outputPath == null) return;

        var outputDir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(outputDir)) return;
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);

        var cts = BeginOperation("Exporting .blend");
        try
        {
            // Same reason as the GLB path: a GBA rider animates by morphing and a
            // weights track addresses every target of the mesh, so a document
            // carries ONE clip. Building all of them into one document silently
            // wrote only the first while reporting the checked count.
            if (character.IsGbaModel)
            {
                var writtenClips = await Task.Run(
                    () => ExportGbaClipFiles(
                        character, animations, outputPath, MeshOutputFormat.Blend, cts.Token),
                    cts.Token);
                MainWindow.Instance?.SetStatus(writtenClips > 0
                    ? $"Exported {writtenClips} clip(s) beside {Path.GetFileName(outputPath)}"
                    : "No GBA clips could be exported.");
                return;
            }

            var result = await Task.Run(() =>
            {
                var (document, error) = CharacterAnimationConverter.BuildDocument(
                    character, animations, visibilityOverrides, animationSourceRig, oneShot);
                if (document == null)
                    throw new InvalidOperationException(error ?? "Convert failed.");

                return ModelExportService.Export(document, new MeshExportRequest
                {
                    OutputDirectory = outputDir,
                    Format = MeshOutputFormat.Blend,
                    OutputStem = outputStem,
                    CancellationToken = cts.Token
                });
            }, cts.Token);

            MainWindow.Instance?.SetStatus(result.OutputPaths.Count > 0
                ? $"Exported {animations.Count} animation(s) → {Path.GetFileName(result.OutputPaths[0])}"
                : "Blend export produced no output.");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Export cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Blend export failed: {ex.Message}");
        }
        finally
        {
            EndOperation(cts);
        }
    }

    /// <summary>
    ///     Writes one file per selected GBA clip, named after the picked file plus
    ///     the clip. A clip that fails to build is skipped rather than aborting
    ///     the others. Returns how many files were written.
    /// </summary>
    /// <remarks>
    ///     Both output formats come through here because the reason is the format's,
    ///     not the writer's: a GBA rider (THPS2's skater, THPS3's rider) has no
    ///     skeleton, so a clip is a set of morph targets and a weights track that
    ///     addresses every one of them — a document carries exactly one clip.
    /// </remarks>
    private static int ExportGbaClipFiles(
        MeshFileEntry character,
        IReadOnlyList<AnimationProbe> animations,
        string outputPath,
        MeshOutputFormat format,
        CancellationToken token)
    {
        var directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
            return 0;
        var stem = Path.GetFileNameWithoutExtension(outputPath);

        var written = 0;
        foreach (var clip in CharacterAnimationConverter.GbaClipIndices(character, animations))
        {
            token.ThrowIfCancellationRequested();
            var (document, _) = CharacterAnimationConverter.BuildGbaClip(character, clip);
            if (document == null)
                continue;

            var name = SanitizeClipName(document.Animations[0].Name);
            if (format == MeshOutputFormat.Glb)
            {
                // The GLB path writes the bytes itself so the file lands beside the
                // picked path with the clip suffix, exactly as before.
                var (glb, triangles) = ModelExportService.BuildGlbBytes(document);
                if (glb == null || triangles == 0)
                    continue;
                File.WriteAllBytes(Path.Combine(directory, $"{stem}__{name}.glb"), glb);
                written++;
                continue;
            }

            var result = ModelExportService.Export(document, new MeshExportRequest
            {
                OutputDirectory = directory,
                Format = format,
                OutputStem = $"{stem}__{name}",
                CancellationToken = token
            });
            if (result.OutputPaths.Count > 0)
                written++;
        }

        return written;
    }

    private static string SanitizeClipName(string name)
    {
        return new string(name.Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
    }

    private CancellationTokenSource BeginOperation(string label)
    {
        var previousCts = _cts;
        var cts = new CancellationTokenSource();
        _cts = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        _progressScope?.Dispose();
        _progressScope = GlobalProgress.Begin(label, indeterminate: true);

        progressBar.Value = 0;
        progressBar.IsIndeterminate = true;
        progressBar.Visibility = Visibility.Visible;
        cancelButton.Visibility = Visibility.Visible;
        return cts;
    }

    private void EndOperation(CancellationTokenSource cts)
    {
        progressBar.IsIndeterminate = false;
        progressBar.Visibility = Visibility.Collapsed;
        cancelButton.Visibility = Visibility.Collapsed;

        // A superseding BeginOperation installs a new CTS before the old
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
