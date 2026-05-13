using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Rendering;

namespace NeversoftMultitool;

public sealed partial class CharacterPreviewTab : UserControl, IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

    private readonly ObservableCollection<AnimationListEntry> _animations = [];
    private readonly ObservableCollection<CharacterEntry> _characters = [];

    private CancellationTokenSource? _animLoadCts;
    private string _inputDir = "";
    private CancellationTokenSource? _operationCts;
    private CharacterPreviewTabPreview? _preview;
    private CharacterEntry? _selectedCharacter;

    public CharacterPreviewTab()
    {
        InitializeComponent();
        CharacterListView.ItemsSource = _characters;
        AnimationListView.ItemsSource = _animations;
        Unloaded += CharacterPreviewTab_Unloaded;
    }

    public void Dispose()
    {
        Unloaded -= CharacterPreviewTab_Unloaded;
        _animLoadCts?.Dispose();
        _animLoadCts = null;
        _operationCts?.Dispose();
        _operationCts = null;
        _preview?.Dispose();
        _preview = null;
    }

    private void CharacterPreviewTab_Unloaded(object sender, RoutedEventArgs e)
        => Dispose();

    // ─── Character source picker ──────────────────────────────────────────

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = path;

        await ResetForNewInputAsync();
        var entries = await Task.Run(() => MeshConverterTabFileScanner.ScanDirectory(path));
        PopulateCharacterList(FilterToSkinnedCharacters(entries));
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;

        await ResetForNewInputAsync();
        var entries = await Task.Run(() => MeshConverterTabFileScanner.ScanArchive(path));
        PopulateCharacterList(FilterToSkinnedCharacters(entries));
    }

    private static IEnumerable<MeshFileEntry> FilterToSkinnedCharacters(IEnumerable<MeshFileEntry> entries)
    {
        return entries.Where(e =>
            e.IsRwDff
            || (e.IsPs2Scene && e.Ps2SubFormat is Ps2SceneSubFormat.ThawSkin
                or Ps2SceneSubFormat.PakSkin
                or Ps2SceneSubFormat.Standard)
            || (e.IsPsx && e.PsxHasHierarchy));
    }

    private async Task ResetForNewInputAsync()
    {
        _characters.Clear();
        _animations.Clear();
        _selectedCharacter = null;
        AnimDiscoveryStatusText.Text = "Select a character to scan for animations.";
        AddAnimFolderButton.IsEnabled = false;
        AddAnimArchiveButton.IsEnabled = false;
        RenderGifButton.IsEnabled = false;
        ConvertGlbButton.IsEnabled = false;
        if (_preview != null) await _preview.ClearAsync();
        UpdateUiState();
    }

    private void PopulateCharacterList(IEnumerable<MeshFileEntry> entries)
    {
        foreach (var entry in entries.OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase))
            _characters.Add(new CharacterEntry { Mesh = entry });

        UpdateUiState();
        MainWindow.Instance?.SetStatus(
            $"Found {_characters.Count} skinned character(s) in {Path.GetFileName(_inputDir)}");
    }

    private void UpdateUiState()
    {
        var hasCharacters = _characters.Count > 0;
        EmptyStatePanel.Visibility = hasCharacters ? Visibility.Collapsed : Visibility.Visible;
        CharacterListCard.Visibility = hasCharacters ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Character selection ──────────────────────────────────────────────

    private async void CharacterListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = CharacterListView.SelectedItem as CharacterEntry;
        _selectedCharacter = entry;
        _animations.Clear();
        RenderGifButton.IsEnabled = false;
        ConvertGlbButton.IsEnabled = false;

        if (entry == null)
        {
            AnimDiscoveryStatusText.Text = "Select a character to scan for animations.";
            AddAnimFolderButton.IsEnabled = false;
            AddAnimArchiveButton.IsEnabled = false;
            if (_preview != null) await _preview.ClearAsync();
            return;
        }

        // Lazy-init preview helper now that the user has shown intent
        _preview ??= new CharacterPreviewTabPreview(
            ModelViewer,
            PreviewLoadingRing,
            PreviewInfoText,
            PreviewErrorText,
            DispatcherQueue);
        await _preview.InitializeAsync();

        // Cancel any prior animation discovery in flight
        var previousAnimCts = _animLoadCts;
        if (previousAnimCts != null)
        {
            _animLoadCts = null;
            await previousAnimCts.CancelAsync();
            previousAnimCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _animLoadCts = cts;
        var token = cts.Token;

        AnimDiscoveryStatusText.Text = "Resolving skeleton…";
        AddAnimFolderButton.IsEnabled = false;
        AddAnimArchiveButton.IsEnabled = false;

        try
        {
            // Resolve skeleton bone count + run discovery on a background thread.
            var (boneCount, probes) = await Task.Run(() =>
            {
                var bc = CharacterAnimationConverter.GetSkeletonBoneCount(entry.Mesh);
                var anims = AnimationDiscovery.FindForCharacter(entry.Mesh.Source, bc, token);
                return (bc, anims);
            }, token);

            if (token.IsCancellationRequested) return;

            entry.SkeletonBoneCount = boneCount;
            AddAnimFolderButton.IsEnabled = true;
            AddAnimArchiveButton.IsEnabled = true;

            MergeAnimationProbes(probes);

            AnimDiscoveryStatusText.Text = _animations.Count == 0
                ? "No animations auto-discovered. Use Add folder… / Add archive… to broaden the search."
                : $"Found {_animations.Count} animation(s)"
                  + (boneCount.HasValue ? $" — skeleton has {boneCount} bones" : "");
        }
        catch (OperationCanceledException)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            AnimDiscoveryStatusText.Text = $"Discovery failed: {ex.Message}";
        }
    }

    // ─── Animation selection (preview) ────────────────────────────────────

    private async void AnimationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Mark the previously-active row inactive
        foreach (var anim in _animations)
            if (anim.IsActive)
                anim.IsActive = false;

        var entry = AnimationListView.SelectedItem as AnimationListEntry;
        if (entry == null || _selectedCharacter == null || _preview == null)
        {
            RenderGifButton.IsEnabled = false;
            return;
        }

        if (!entry.MatchesSkeleton)
        {
            PreviewErrorText.Text = entry.MismatchTooltip;
            PreviewErrorText.Visibility = Visibility.Visible;
            RenderGifButton.IsEnabled = false;
            return;
        }

        entry.IsActive = true;
        await _preview.LoadPreviewAsync(_selectedCharacter.Mesh, entry.Probe);
        RenderGifButton.IsEnabled = _preview.LastGlbBytes != null;
    }

    // ─── Manual anim folder / archive add ─────────────────────────────────

    private async void AddAnimFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacter == null) return;
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        var boneCount = _selectedCharacter.SkeletonBoneCount;
        var probes = await Task.Run(() => AnimationDiscovery.FindInDirectory(path, boneCount, CancellationToken.None));
        MergeAnimationProbes(probes);
        AnimDiscoveryStatusText.Text = $"{_animations.Count} animation(s) listed.";
    }

    private async void AddAnimArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacter == null) return;
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        var boneCount = _selectedCharacter.SkeletonBoneCount;
        var probes = await Task.Run(() =>
        {
            var backend = ArchiveAssetBackend.TryOpen(path);
            return backend == null
                ? []
                : (IReadOnlyList<AnimationProbe>)AnimationDiscovery.FindInArchive(
                    backend, boneCount, CancellationToken.None);
        });
        MergeAnimationProbes(probes);
        AnimDiscoveryStatusText.Text = $"{_animations.Count} animation(s) listed.";
    }

    private void MergeAnimationProbes(IEnumerable<AnimationProbe> probes)
    {
        // Dedupe by source DisplayName so re-scans / additions don't double-list.
        var seen = _animations
            .Select(a => a.Probe.Source.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var probe in probes
                     .OrderBy(p => p.MatchesSkeleton ? 0 : 1)
                     .ThenBy(p => p.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            if (!seen.Add(probe.Source.DisplayName)) continue;
            _animations.Add(new AnimationListEntry { Probe = probe });
        }

        ConvertGlbButton.IsEnabled = _animations.Any(a => a.MatchesSkeleton);
    }

    // ─── Export actions ───────────────────────────────────────────────────

    private async void RenderGif_Click(object sender, RoutedEventArgs e)
    {
        if (_preview?.LastGlbBytes == null || _selectedCharacter == null) return;
        var activeAnim = _animations.FirstOrDefault(a => a.IsActive);
        if (activeAnim == null) return;

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(_selectedCharacter.FileName);
        var animStem = StripAnimExtension(activeAnim.DisplayName);
        var outputPath = Path.Combine(outputDir, $"{characterStem}_{animStem}.gif");

        OperationProgress.Value = 0;
        OperationProgress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;
        var glbBytes = _preview.LastGlbBytes;

        var cts = new CancellationTokenSource();
        var previousCts = _operationCts;
        _operationCts = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        try
        {
            var (frames, duration) = await Task.Run(() =>
            {
                var tempGlb = Path.Combine(
                    Path.GetTempPath(), "NeversoftMultitool", "CharacterPreview",
                    $"{Guid.NewGuid():N}.glb");
                Directory.CreateDirectory(Path.GetDirectoryName(tempGlb)!);
                File.WriteAllBytes(tempGlb, glbBytes);
                try
                {
                    return GlbGifRenderer.RenderToFile(tempGlb, outputPath);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(tempGlb)) File.Delete(tempGlb);
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }, cts.Token);

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
            OperationProgress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            if (_operationCts == cts) _operationCts = null;
            cts.Dispose();
        }
    }

    private async void ConvertGlb_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedCharacter == null) return;

        var checkedAnims = _animations
            .Where(a => a.IsChecked && a.MatchesSkeleton)
            .Select(a => a.Probe)
            .ToList();
        if (checkedAnims.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one matching animation to export.");
            return;
        }

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var characterStem = MeshConverterTabFileScanner.StripCompoundExtension(_selectedCharacter.FileName);
        var outputPath = Path.Combine(outputDir, $"{characterStem}.glb");
        var character = _selectedCharacter.Mesh;

        OperationProgress.Value = 0;
        OperationProgress.Visibility = Visibility.Visible;
        CancelButton.Visibility = Visibility.Visible;

        var cts = new CancellationTokenSource();
        var previousCts = _operationCts;
        _operationCts = cts;
        previousCts?.Cancel();
        previousCts?.Dispose();

        try
        {
            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(character, checkedAnims),
                cts.Token);

            if (result.GlbBytes == null)
            {
                MainWindow.Instance?.SetStatus(result.Error ?? "Convert failed.");
                return;
            }

            await File.WriteAllBytesAsync(outputPath, result.GlbBytes, cts.Token);
            MainWindow.Instance?.SetStatus(
                $"Exported {checkedAnims.Count} animation(s) → {Path.GetFileName(outputPath)}");
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
            OperationProgress.Visibility = Visibility.Collapsed;
            CancelButton.Visibility = Visibility.Collapsed;
            if (_operationCts == cts) _operationCts = null;
            cts.Dispose();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        var cts = _operationCts;
        if (cts == null) return;
        _operationCts = null;
        cts.Cancel();
        cts.Dispose();
    }

    private static string StripAnimExtension(string fileName)
    {
        var idx = fileName.IndexOf(".ska", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? fileName[..idx] : Path.GetFileNameWithoutExtension(fileName);
    }
}
