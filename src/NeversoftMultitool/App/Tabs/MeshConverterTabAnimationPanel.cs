using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
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
    FrameworkElement sourceRigControls,
    TextBlock sourceRigText,
    Button chooseSourceRigButton,
    Button chooseArchiveSourceRigButton,
    Button clearSourceRigButton,
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
    private bool _characterReady;
    private CancellationTokenSource? _loadCts;
    private long _stateGeneration;

    public ObservableCollection<AnimationListEntry> Animations { get; } = [];

    public MeshFileEntry? Character { get; private set; }
    public SkaAnimationSourceRig? SourceRig { get; private set; }

    public int? EffectiveAnimationBoneCount =>
        SourceRig?.BoneCount ?? Character?.SkeletonBoneCount;

    /// <summary>The animation to auto-preview after discovery, or null.</summary>
    public AnimationListEntry? FirstMatch =>
        Animations.FirstOrDefault(a => a.MatchesSkeleton);

    public AnimationListEntry? ActiveEntry => Animations.FirstOrDefault(a => a.IsActive);

    /// <summary>Filter combo index 1 = hide single-frame pose slots.</summary>
    private bool HideSinglePoses => filterCombo.SelectedIndex == 1;

    public void Dispose()
    {
        _stateGeneration++;
        CancelPendingLoad();
    }

    public void Reset(string status)
    {
        _stateGeneration++;
        CancelPendingLoad();
        Character = null;
        SourceRig = null;
        _characterReady = false;
        _allProbes.Clear();
        Animations.Clear();
        statusText.Text = status;
        sourceRigControls.Visibility = Visibility.Collapsed;
        sourceRigText.Text = "Animation rig: character skeleton";
        ToolTipService.SetToolTip(sourceRigText, null);
        ApplyOperationControlState(operationActive: false);
        exportGlbButton.IsEnabled = false;
        exportBlendButton.IsEnabled = false;
    }

    /// <summary>
    ///     Resolves the skeleton and discovers animations for a character.
    ///     Returns true when discovery completed (not cancelled/superseded).
    /// </summary>
    public async Task<bool> LoadForCharacterAsync(MeshFileEntry entry)
    {
        var (cts, generation) = BeginStateOperation();
        Character = entry;
        SourceRig = null;
        _characterReady = false;
        _allProbes.Clear();
        Animations.Clear();
        exportGlbButton.IsEnabled = false;
        exportBlendButton.IsEnabled = false;
        sourceRigControls.Visibility = entry.IsPs2Scene ? Visibility.Visible : Visibility.Collapsed;
        sourceRigText.Text = "Animation rig: character skeleton";
        ToolTipService.SetToolTip(sourceRigText, null);
        ApplyOperationControlState(operationActive: true);
        statusText.Text = "Resolving skeleton…";
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

            if (!IsCurrentOperation(entry, cts, generation)) return false;

            entry.SkeletonBoneCount = boneCount;
            entry.SkeletonProbed = true;
            _characterReady = true;
            // This first N64 slice is embedded-only. Do not let the manual
            // SKA/PSX bank pickers imply unsupported cross-bank binding.

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
            if (IsCurrentOperation(entry, cts, generation))
                statusText.Text = $"Discovery failed: {ex.Message}";
            return false;
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    public async Task AddFolderAsync()
    {
        var character = Character;
        if (character == null) return;
        if (character.IsN64Model) return;
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null || !ReferenceEquals(Character, character)) return;

        var (cts, generation) = BeginStateOperation();
        var token = cts.Token;
        var boneCount = EffectiveAnimationBoneCount;
        var isPsx = character.IsPsx;
        var characterSource = character.Source;
        try
        {
            var probes = await Task.Run(() => AnimationDiscovery.FindInDirectory(
                path,
                boneCount,
                token,
                includePsxAnimationBanks: isPsx,
                targetCharacterSource: isPsx ? characterSource : null), token);
            if (!IsCurrentOperation(character, cts, generation)) return;

            MergeAnimationProbes(probes);
            UpdateStatus(boneCount);
        }
        catch (OperationCanceledException)
        {
            // Expected when the character or animation rig changes.
        }
        catch (Exception ex)
        {
            if (IsCurrentOperation(character, cts, generation))
                statusText.Text = $"Animation folder scan failed: {ex.Message}";
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    public async Task AddArchiveAsync()
    {
        var character = Character;
        if (character == null) return;
        if (character.IsN64Model) return;
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null || !ReferenceEquals(Character, character)) return;

        var (cts, generation) = BeginStateOperation();
        var token = cts.Token;
        var boneCount = EffectiveAnimationBoneCount;
        var isPsx = character.IsPsx;
        var characterSource = character.Source;
        var targetCharacterSource = isPsx ? characterSource : null;
        try
        {
            var probes = await Task.Run<IReadOnlyList<AnimationProbe>>(() =>
            {
                var backend = ArchiveAssetBackend.TryOpen(path);
                if (backend == null) return [];

                return AnimationDiscovery.FindInArchive(
                    backend,
                    boneCount,
                    token,
                    includePsxAnimationBanks: isPsx,
                    targetCharacterSource: targetCharacterSource);
            }, token);
            if (!IsCurrentOperation(character, cts, generation)) return;

            MergeAnimationProbes(probes);
            UpdateStatus(boneCount);
        }
        catch (OperationCanceledException)
        {
            // Expected when the character or animation rig changes.
        }
        catch (Exception ex)
        {
            if (IsCurrentOperation(character, cts, generation))
                statusText.Text = $"Animation archive scan failed: {ex.Message}";
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    /// <summary>
    ///     Transactionally validates and installs the skeleton that authored
    ///     selected SKA tracks. A failed or superseded load keeps the old rig.
    /// </summary>
    public Task<bool> TrySetSourceRigAsync(
        AssetSource source,
        Func<bool>? remainsSelected = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        return TryAcquireSourceRigAsync(
            async (validateSourceAsync, token) =>
                (SkaAnimationSourceRig?)await validateSourceAsync(source, token),
            remainsSelected);
    }

    /// <summary>
    ///     Runs source acquisition, parsing, and target-map validation under one
    ///     panel-owned operation. Archive callers keep their catalog alive while
    ///     awaiting <paramref name="acquireValidatedRigAsync" /> and invoke the
    ///     supplied validator before releasing its backend handles.
    /// </summary>
    public async Task<bool> TryAcquireSourceRigAsync(
        Func<
            Func<AssetSource, CancellationToken, Task<SkaAnimationSourceRig>>,
            CancellationToken,
            Task<SkaAnimationSourceRig?>> acquireValidatedRigAsync,
        Func<bool>? remainsSelected = null)
    {
        ArgumentNullException.ThrowIfNull(acquireValidatedRigAsync);
        var character = Character;
        if (character is not { IsPs2Scene: true } || remainsSelected?.Invoke() == false)
            return false;

        var (cts, generation) = BeginStateOperation();
        var token = cts.Token;
        statusText.Text = "Choosing animation rig…";

        try
        {
            Task<SkaAnimationSourceRig> ValidateSourceAsync(
                AssetSource source,
                CancellationToken validationToken) =>
                Task.Run(() => LoadAndValidateSourceRig(character, source), validationToken);

            var rig = await acquireValidatedRigAsync(ValidateSourceAsync, token);

            if (!IsCurrentOperation(character, cts, generation)
                || remainsSelected?.Invoke() == false)
                return false;

            // Picker cancellation is not a state change. Restore status from the
            // durable rig/probe state and leave all selections untouched.
            if (rig == null)
            {
                UpdateStatus(EffectiveAnimationBoneCount);
                return false;
            }

            SourceRig = rig;
            ReclassifyProbes(rig.BoneCount);
            var displayName = rig.SourceDisplayName.Contains("::", StringComparison.Ordinal)
                ? rig.SourceDisplayName
                : Path.GetFileName(rig.SourceDisplayName);
            sourceRigText.Text =
                $"Animation rig: {displayName} ({rig.BoneCount} bones)";
            ToolTipService.SetToolTip(sourceRigText, rig.SourceDisplayName);
            RebuildList(preserveChecked: false);
            UpdateStatus(rig.BoneCount);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            if (IsCurrentOperation(character, cts, generation))
                statusText.Text = $"Animation rig rejected: {ex.Message}";

            return false;
        }
        finally
        {
            CompleteOperation(cts);
        }
    }

    private static SkaAnimationSourceRig LoadAndValidateSourceRig(
        MeshFileEntry character,
        AssetSource source)
    {
        var parsedRig = SkaAnimationSourceRig.Load(source);
        var stem = MeshConverterTabFileScanner.StripCompoundExtension(character.FileName);
        var target = MeshConverterTabFileConverter.TryLoadPs2Skeleton(character, stem)
                     ?? throw new InvalidDataException(
                         "The selected character's target skeleton could not be resolved.");
        _ = SkaAnimationBindingPlan.Create(target, parsedRig);
        return parsedRig;
    }

    public bool ClearSourceRig()
    {
        if (Character is not { IsPs2Scene: true } || SourceRig == null)
            return false;

        _stateGeneration++;
        CancelPendingLoad();
        SourceRig = null;
        ReclassifyProbes(Character.SkeletonBoneCount);
        sourceRigText.Text = "Animation rig: character skeleton";
        ToolTipService.SetToolTip(sourceRigText, null);
        ApplyOperationControlState(operationActive: false);
        RebuildList(preserveChecked: false);
        UpdateStatus(Character.SkeletonBoneCount);
        return true;
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
        UpdateStatus(EffectiveAnimationBoneCount);
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

    private void RebuildList(bool preserveChecked = true)
    {
        var showAll = showAllCheckBox.IsChecked == true;
        var hidePoses = HideSinglePoses;
        var previouslyChecked = preserveChecked
            ? Animations
                .Where(a => a.IsChecked)
                .Select(a => a.Probe.Source.DisplayName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : [];

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
            statusText.Text = Character?.IsN64Model == true
                ? "No eligible embedded N64 animation clips found. External banks and "
                  + "shells with ambiguous matrix addressing are not supported."
                : "No animations auto-discovered. Use Add folder… / Add archive… to broaden the search.";
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

        if (SourceRig != null && Character?.SkeletonBoneCount is { } targetBoneCount)
            text += $" — animation rig has {boneCount} bones; character has {targetBoneCount} bones";
        else if (boneCount.HasValue)
            text += $" — skeleton has {boneCount} bones";
        statusText.Text = text;
    }

    private void ReclassifyProbes(int? expectedBoneCount)
    {
        for (var index = 0; index < _allProbes.Count; index++)
            _allProbes[index] = _allProbes[index].WithExpectedBoneCount(expectedBoneCount);
    }

    private (CancellationTokenSource Cts, long Generation) BeginStateOperation()
    {
        _stateGeneration++;
        CancelPendingLoad();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        ApplyOperationControlState(operationActive: true);
        return (cts, _stateGeneration);
    }

    private bool IsCurrentOperation(
        MeshFileEntry character,
        CancellationTokenSource cts,
        long generation) =>
        !cts.IsCancellationRequested
        && ReferenceEquals(Character, character)
        && ReferenceEquals(_loadCts, cts)
        && _stateGeneration == generation;

    private void CompleteOperation(CancellationTokenSource cts)
    {
        var wasCurrent = ReferenceEquals(_loadCts, cts);
        if (wasCurrent)
            _loadCts = null;
        cts.Dispose();
        if (wasCurrent)
            ApplyOperationControlState(operationActive: false);
    }

    private void CancelPendingLoad()
    {
        var cts = _loadCts;
        _loadCts = null;
        if (cts == null) return;
        cts.Cancel();
    }

    private void ApplyOperationControlState(bool operationActive)
    {
        var state = AnimationPanelOperationControlState.Create(
            _characterReady,
            Character?.IsN64Model == true,
            Character?.IsPs2Scene == true,
            Character?.SkeletonBoneCount.HasValue == true,
            SourceRig != null,
            operationActive);
        addFolderButton.IsEnabled = state.AddExternalSourcesEnabled;
        addArchiveButton.IsEnabled = state.AddExternalSourcesEnabled;
        chooseSourceRigButton.IsEnabled = state.ChooseSourceRigEnabled;
        chooseArchiveSourceRigButton.IsEnabled = state.ChooseArchiveSourceRigEnabled;
        clearSourceRigButton.IsEnabled = state.ClearSourceRigEnabled;
    }
}
