using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool;

/// <summary>
///     Owns the merged tab's Animations pane: skeleton resolution + animation
///     discovery for the selected character, the (bone-count-filtered) list,
///     manual folder/archive additions, and select-all/none. Ported from the
///     Character Preview tab; the filter change is deliberate — mismatched
///     animations are hidden by default instead of merely greyed out, with a
///     "Show all" toggle to bring them back. The filter combo can additionally
///     hide single-frame pose slots; hidden rows are excluded from select-all
///     and from export.
/// </summary>
internal sealed class MeshConverterTabAnimationPanel(
    TextBlock statusText,
    Button addFolderButton,
    Button addArchiveButton,
    Button exportGlbButton,
    Button exportBlendButton,
    CheckBox showAllCheckBox,
    ComboBox filterCombo,
    Func<bool> blendExportAvailable) : IDisposable
{
    private static readonly string[] ArchiveExtensions = [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"];

    private readonly List<AnimationProbe> _allProbes = [];
    private CancellationTokenSource? _loadCts;

    public ObservableCollection<AnimationListEntry> Animations { get; } = [];

    public MeshFileEntry? Character { get; private set; }

    /// <summary>The animation to auto-preview after discovery, or null.</summary>
    public AnimationListEntry? FirstMatch =>
        Animations.FirstOrDefault(a => a.MatchesSkeleton);

    public AnimationListEntry? ActiveEntry => Animations.FirstOrDefault(a => a.IsActive);

    /// <summary>Filter combo index 1 = hide single-frame pose slots.</summary>
    private bool HideSinglePoses => filterCombo.SelectedIndex == 1;

    public void Dispose()
    {
        _loadCts?.Dispose();
        _loadCts = null;
    }

    public void Reset(string status)
    {
        Character = null;
        _allProbes.Clear();
        Animations.Clear();
        statusText.Text = status;
        addFolderButton.IsEnabled = false;
        addArchiveButton.IsEnabled = false;
        exportGlbButton.IsEnabled = false;
        exportBlendButton.IsEnabled = false;
    }

    /// <summary>
    ///     Resolves the skeleton and discovers animations for a character.
    ///     Returns true when discovery completed (not cancelled/superseded).
    /// </summary>
    public async Task<bool> LoadForCharacterAsync(MeshFileEntry entry)
    {
        Character = entry;
        _allProbes.Clear();
        Animations.Clear();
        exportGlbButton.IsEnabled = false;
        exportBlendButton.IsEnabled = false;
        addFolderButton.IsEnabled = false;
        addArchiveButton.IsEnabled = false;
        statusText.Text = "Resolving skeleton…";

        var previousCts = _loadCts;
        if (previousCts != null)
        {
            _loadCts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _loadCts = cts;
        var token = cts.Token;

        try
        {
            var (boneCount, probes) = await Task.Run(() =>
            {
                var bc = entry.SkeletonProbed
                    ? entry.SkeletonBoneCount
                    : CharacterAnimationConverter.GetSkeletonBoneCount(entry);
                var anims = AnimationDiscovery.FindForCharacter(entry.Source, bc, token);
                return (bc, anims);
            }, token);

            if (token.IsCancellationRequested) return false;

            entry.SkeletonBoneCount = boneCount;
            entry.SkeletonProbed = true;
            addFolderButton.IsEnabled = true;
            addArchiveButton.IsEnabled = true;

            MergeAnimationProbes(probes);
            UpdateStatus(boneCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            statusText.Text = $"Discovery failed: {ex.Message}";
            return false;
        }
    }

    public async Task AddFolderAsync()
    {
        if (Character == null) return;
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        var boneCount = Character.SkeletonBoneCount;
        var isPsx = Character.IsPsx;
        var characterSource = Character.Source;
        var probes = await Task.Run(() => AnimationDiscovery.FindInDirectory(
            path,
            boneCount,
            CancellationToken.None,
            includePsxAnimationBanks: isPsx,
            targetCharacterSource: isPsx ? characterSource : null));
        MergeAnimationProbes(probes);
        UpdateStatus(boneCount);
    }

    public async Task AddArchiveAsync()
    {
        if (Character == null) return;
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        var boneCount = Character.SkeletonBoneCount;
        var isPsx = Character.IsPsx;
        var characterSource = Character.Source;
        var targetCharacterSource = isPsx ? characterSource : null;
        var probes = await Task.Run(() =>
        {
            var backend = ArchiveAssetBackend.TryOpen(path);
            if (backend == null) return [];

            return AnimationDiscovery.FindInArchive(
                backend,
                boneCount,
                CancellationToken.None,
                includePsxAnimationBanks: isPsx,
                targetCharacterSource: targetCharacterSource);
        });
        MergeAnimationProbes(probes);
        UpdateStatus(boneCount);
    }

    public void SetAllChecked(bool isChecked)
    {
        foreach (var anim in Animations)
        {
            if (anim.MatchesSkeleton)
                anim.IsChecked = isChecked;
        }
    }

    /// <summary>
    ///     Re-applies the show-all / single-pose filters to the already-
    ///     discovered set. No-op before a character is selected so the initial
    ///     filter-combo selection can't clobber the "select a character" status.
    /// </summary>
    public void RefreshFilter()
    {
        if (Character == null) return;
        RebuildList();
        UpdateStatus(Character.SkeletonBoneCount);
    }

    public IReadOnlyList<AnimationProbe> CheckedMatchingProbes()
    {
        return Animations
            .Where(a => a.IsChecked && a.MatchesSkeleton)
            .Select(a => a.Probe)
            .ToList();
    }

    private void MergeAnimationProbes(IEnumerable<AnimationProbe> probes)
    {
        // Accumulate against everything seen (not just the visible rows) so
        // re-scans and filter toggles don't double-list.
        var seen = _allProbes
            .Select(p => p.Source.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var probe in probes.Where(probe => seen.Add(probe.Source.DisplayName)))
        {
            _allProbes.Add(probe);
        }

        RebuildList();
    }

    private void RebuildList()
    {
        var showAll = showAllCheckBox.IsChecked == true;
        var hidePoses = HideSinglePoses;
        var previouslyChecked = Animations
            .Where(a => a.IsChecked)
            .Select(a => a.Probe.Source.DisplayName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Animations.Clear();
        foreach (var probe in _allProbes
                     .Where(p => showAll || p.MatchesSkeleton)
                     .Where(p => !hidePoses || !p.IsSinglePose)
                     .OrderBy(p => p.MatchesSkeleton ? 0 : 1)
                     .ThenBy(p => p.DisplayName, NaturalStringComparer.OrdinalIgnoreCase))
        {
            Animations.Add(new AnimationListEntry
            {
                Probe = probe,
                IsChecked = previouslyChecked.Contains(probe.Source.DisplayName)
            });
        }

        var hasMatching = Animations.Any(a => a.MatchesSkeleton);
        exportGlbButton.IsEnabled = hasMatching;
        exportBlendButton.IsEnabled = hasMatching && blendExportAvailable();
    }

    private void UpdateStatus(int? boneCount)
    {
        var hiddenMismatches = _allProbes.Count(p => !p.MatchesSkeleton);
        var showAll = showAllCheckBox.IsChecked == true;

        if (_allProbes.Count == 0)
        {
            statusText.Text =
                "No animations auto-discovered. Use Add folder… / Add archive… to broaden the search.";
            return;
        }

        var matching = _allProbes.Count - hiddenMismatches;
        var text = $"Found {matching} matching animation(s)";
        if (hiddenMismatches > 0)
            text += showAll
                ? $" ({hiddenMismatches} mismatched shown greyed)"
                : $" ({hiddenMismatches} hidden: bone-count mismatch)";
        if (HideSinglePoses)
        {
            var hiddenPoses = _allProbes.Count(p =>
                p.IsSinglePose && (showAll || p.MatchesSkeleton));
            if (hiddenPoses > 0)
                text += $" ({hiddenPoses} single-frame pose(s) hidden)";
        }

        if (boneCount.HasValue)
            text += $" — skeleton has {boneCount} bones";
        statusText.Text = text;
    }
}
