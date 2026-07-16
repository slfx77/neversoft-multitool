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

    public void Dispose()
    {
        _cts?.Dispose();
        _cts = null;
    }

    public void Cancel()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        cts.Cancel();
        cts.Dispose();
    }

    public async Task ExportGlbAsync(MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        if (animations.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one matching animation to export.");
            return;
        }

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var outputPath = Path.Combine(outputDir, $"{characterStem}.glb");

        var cts = BeginOperation();
        try
        {
            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(character, animations),
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

    public async Task ExportBlendAsync(MeshFileEntry character, IReadOnlyList<AnimationProbe> animations)
    {
        if (animations.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one matching animation to export.");
            return;
        }

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);

        var cts = BeginOperation();
        try
        {
            var result = await Task.Run(() =>
            {
                var (document, error) = CharacterAnimationConverter.BuildDocument(character, animations);
                if (document == null)
                    throw new InvalidOperationException(error ?? "Convert failed.");

                return ModelExportService.Export(document, new MeshExportRequest
                {
                    OutputDirectory = outputDir,
                    Format = MeshOutputFormat.Blend,
                    OutputStem = characterStem,
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

    private CancellationTokenSource BeginOperation()
    {
        var previousCts = _cts;
        var cts = new CancellationTokenSource();
        _cts = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

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
        if (ReferenceEquals(_cts, cts)) _cts = null;
        cts.Dispose();
    }
}
