using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool;

/// <summary>
///     Merged mesh + character tab: one file list (with batch checkboxes), one
///     3D viewer, and a tabbed right panel (Animations / Display Settings / Export).
///     Selecting a skinned character resolves its skeleton and populates the
///     Animations pane; everything else previews statically.
/// </summary>
public sealed partial class MeshConverterTab : UserControl, IDisposable
{
    private readonly MeshConverterTabAnimationExporter _animExporter;
    private readonly MeshConverterTabAnimationPanel _animPanel;
    private readonly MeshConverterTabBatchRunner _batchRunner;
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private readonly Dictionary<string, bool> _visibilityOverrides =
        new(StringComparer.Ordinal);
    private MeshConverterTabPreview? _preview;
    private CancellationTokenSource? _scanCts;
    private bool _suppressAnimationSelectionChanged;
    private MeshFileEntry? _visibilityEntry;

    public MeshConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;

        _animPanel = new MeshConverterTabAnimationPanel(
            AnimDiscoveryStatusText,
            AddAnimFolderButton,
            AddAnimArchiveButton,
            ConvertGlbButton,
            ConvertBlendButton,
            ShowAllAnimsCheckBox);
        AnimationListView.ItemsSource = _animPanel.Animations;

        _animExporter = new MeshConverterTabAnimationExporter(ConversionProgress, CancelButton);
        _batchRunner = new MeshConverterTabBatchRunner(
            ConversionProgress, ConvertButton, CancelButton, DispatcherQueue);

        ModelViewer.ModelLoaded += ModelViewer_ModelLoaded;
        Unloaded += MeshConverterTab_Unloaded;

        // Selected here rather than in XAML so SelectionChanged can't fire
        // mid-InitializeComponent against not-yet-created elements.
        PanelSelector.SelectedItem = ExportPanelItem;
    }

    public void Dispose()
    {
        Unloaded -= MeshConverterTab_Unloaded;
        ModelViewer.ModelLoaded -= ModelViewer_ModelLoaded;
        _scanCts?.Dispose();
        _scanCts = null;
        _preview?.Dispose();
        _preview = null;
        _animPanel.Dispose();
        _animExporter.Dispose();
        _batchRunner.Dispose();
    }

    // ─── Scanning ─────────────────────────────────────────────────────────

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        InputPathText.Text = path;
        await RunRecursiveScan(path);
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(
            [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"]);
        if (path == null) return;

        InputPathText.Text = path;

        await CancelInFlightScan();

        _items.Clear();
        ConvertButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        try
        {
            MainWindow.Instance?.SetStatus($"Scanning {Path.GetFileName(path)}...");

            var progress = new Progress<int>(count =>
                MainWindow.Instance?.SetStatus($"Scanning {Path.GetFileName(path)}: {count} entries probed..."));

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanArchive(path, progress, token),
                token);

            token.ThrowIfCancellationRequested();

            foreach (var entry in entries)
                _items.Add(entry);

            if (entries.Count == 0)
                MainWindow.Instance?.SetStatus(
                    $"{Path.GetFileName(path)}: no supported mesh entries found.");
            else
                MainWindow.Instance?.SetStatus(
                    $"Found {entries.Count} mesh entrie(s) in {Path.GetFileName(path)}.");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Scan cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    private async Task RunRecursiveScan(string rootDir)
    {
        await CancelInFlightScan();

        _items.Clear();
        ConvertButton.IsEnabled = false;

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        MainWindow.Instance?.SetStatus("Scanning directory...");

        try
        {
            var scanSummary = await Task.Run(
                () => MeshConverterTabFileScanner.AnalyzeDirectory(rootDir, token),
                token);

            if (scanSummary.UnsupportedFiles.Count > 0)
            {
                var proceed = await ScanSummaryDialog.ShowIfNeeded(
                    XamlRoot,
                    scanSummary.SupportedCount,
                    [.. scanSummary.UnsupportedFiles]);
                if (!proceed)
                {
                    MainWindow.Instance?.SetStatus("Scan cancelled");
                    return;
                }
            }

            var progress = new Progress<int>(count =>
                MainWindow.Instance?.SetStatus($"Scanning: {count} files probed..."));

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanDirectory(rootDir, progress, token),
                token);

            token.ThrowIfCancellationRequested();

            foreach (var entry in entries)
                _items.Add(entry);

            MainWindow.Instance?.SetStatus($"Found {entries.Count} mesh file(s).");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Scan cancelled");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Scan failed: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_scanCts, cts))
                _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    private async Task CancelInFlightScan()
    {
        var existing = _scanCts;
        if (existing == null) return;
        _scanCts = null;
        try
        {
            await existing.CancelAsync();
        }
        catch
        {
            // swallow
        }

        existing.Dispose();
    }

    // ─── I/O + shared UI state ────────────────────────────────────────────

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasChecked = _items.Any(i => i.IsChecked);
        var hasFormat = ExportGlbCheckbox.IsChecked == true || ExportBlendCheckbox.IsChecked == true;

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasChecked && hasFormat;
        UpdateWorldzoneExportSettingsVisibility();
        UpdateLevelObjectExportSettingsVisibility();
        UpdateRenderButtons();
    }

    private void ExportFormatCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        // Fires during InitializeComponent before later-declared elements exist.
        if (ConvertButton == null) return;
        UpdateUiState();
    }

    private void FileCheckbox_Click(object sender, RoutedEventArgs e)
    {
        UpdateUiState();
    }

    private void FilesSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.IsChecked = true;
        UpdateUiState();
    }

    private void FilesSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            item.IsChecked = false;
        UpdateUiState();
    }

    // ─── Right panel pane switching ───────────────────────────────────────

    private void PanelSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (AnimationsPane == null || SettingsPane == null || ExportPane == null) return;

        var selected = PanelSelector.SelectedItem;
        AnimationsPane.Visibility = ReferenceEquals(selected, AnimationsPanelItem)
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsPane.Visibility = ReferenceEquals(selected, SettingsPanelItem)
            ? Visibility.Visible
            : Visibility.Collapsed;
        ExportPane.Visibility = ReferenceEquals(selected, ExportPanelItem)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateDisplaySettingsVisibility(MeshFileEntry? entry)
    {
        WorldzoneViewportSettingsSection.Visibility = entry?.IsPakWorldzone == true
            ? Visibility.Visible
            : Visibility.Collapsed;
        DisplayIncludeLevelObjectsCheckbox.Visibility =
            entry?.HasSupportedLevelObjectCompanion == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        UpdateWorldzoneExportSettingsVisibility();
        UpdateLevelObjectExportSettingsVisibility();
    }

    private void UpdateWorldzoneExportSettingsVisibility()
    {
        // Export settings apply to checked rows, which need not be the row
        // currently selected for preview. Keep the viewport control tied to
        // selection, but expose export lighting/scale whenever the operation
        // scope contains an actual content-detected worldzone.
        if (WorldzoneExportSettingsSection == null)
            return;

        WorldzoneExportSettingsSection.Visibility = _items.Any(static entry =>
            entry.IsChecked && entry.IsPakWorldzone)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateLevelObjectExportSettingsVisibility()
    {
        if (ExportIncludeLevelObjectsCheckbox == null)
            return;

        ExportIncludeLevelObjectsCheckbox.Visibility = _items.Any(static entry =>
            entry.IsChecked && entry.HasSupportedLevelObjectCompanion)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void ResetVisibilityGroups(MeshFileEntry? entry, string? status = null)
    {
        _visibilityEntry = entry;
        _visibilityOverrides.Clear();
        VisibilityGroupsList.Children.Clear();
        VisibilityGroupsStatusText.Text = status ?? (entry == null
            ? "Select a mesh to inspect its visibility groups."
            : "Loading visibility groups...");
        VisibilityGroupsStatusText.Visibility = Visibility.Visible;
    }

    private void ApplyVisibilityGroups(
        MeshFileEntry entry,
        IReadOnlyList<ModelVisibilityGroup> groups)
    {
        if (!ReferenceEquals(FilesListView.SelectedItem, entry) ||
            !ReferenceEquals(_visibilityEntry, entry))
        {
            return;
        }

        _visibilityOverrides.Clear();
        VisibilityGroupsList.Children.Clear();

        foreach (var group in groups)
        {
            _visibilityOverrides[group.Id] = group.IsEnabled;
            var toggle = new CheckBox
            {
                Content = group.Label,
                IsChecked = group.IsEnabled,
                MinWidth = 0,
                Tag = group
            };
            if (!string.IsNullOrWhiteSpace(group.SourceReference))
                ToolTipService.SetToolTip(toggle, group.SourceReference);
            toggle.Click += VisibilityGroupToggle_Click;
            VisibilityGroupsList.Children.Add(toggle);
        }

        VisibilityGroupsStatusText.Text = groups.Count == 0
            ? "No visibility groups in this mesh."
            : "Toggle source-authored geometry variants for preview and export.";
        VisibilityGroupsStatusText.Visibility = Visibility.Visible;
    }

    private Dictionary<string, bool>? GetVisibilityOverridesSnapshot(
        MeshFileEntry entry)
    {
        return ReferenceEquals(_visibilityEntry, entry) && _visibilityOverrides.Count > 0
            ? new Dictionary<string, bool>(_visibilityOverrides, StringComparer.Ordinal)
            : null;
    }

    private async void VisibilityGroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ModelVisibilityGroup group } toggle ||
            _visibilityEntry is not { } entry ||
            !ReferenceEquals(FilesListView.SelectedItem, entry))
        {
            return;
        }

        var isEnabled = toggle.IsChecked == true;
        if (isEnabled && group.ExclusiveSetId is { } exclusiveSetId)
        {
            foreach (var sibling in VisibilityGroupsList.Children.OfType<CheckBox>())
            {
                if (ReferenceEquals(sibling, toggle)
                    || sibling.Tag is not ModelVisibilityGroup siblingGroup
                    || !string.Equals(
                        siblingGroup.ExclusiveSetId,
                        exclusiveSetId,
                        StringComparison.Ordinal))
                {
                    continue;
                }

                sibling.IsChecked = false;
                _visibilityOverrides[siblingGroup.Id] = false;
            }
        }

        _visibilityOverrides[group.Id] = isEnabled;
        await ReloadPreviewForVisibilityAsync(entry);
    }

    private async Task ReloadPreviewForVisibilityAsync(MeshFileEntry entry)
    {
        if (!ReferenceEquals(FilesListView.SelectedItem, entry)) return;

        var activeAnimation = _animPanel.ActiveEntry;
        if (entry.IsAnimatableCharacter &&
            activeAnimation != null &&
            ReferenceEquals(_animPanel.Character, entry))
        {
            await LoadAnimationPreviewAsync(
                entry,
                activeAnimation,
                preserveCamera: true);
        }
        else
        {
            await LoadStaticPreviewAsync(
                entry,
                entry.IsPakWorldzone
                    ? GetSelectedPreviewWorldzoneTimeOfDay()
                    : WorldzoneTimeOfDay.All,
                preserveCamera: true);
        }

        UpdateRenderButtons();
    }

    private async Task LoadStaticPreviewAsync(
        MeshFileEntry entry,
        WorldzoneTimeOfDay worldzoneTimeOfDay = WorldzoneTimeOfDay.All,
        bool preserveCamera = false)
    {
        if (_preview == null) return;

        var groups = await _preview.LoadPreviewAsync(
            entry,
            worldzoneTimeOfDay,
            GetVisibilityOverridesSnapshot(entry),
            preserveCamera,
            includeLevelObjects: DisplayIncludeLevelObjectsCheckbox.IsChecked != false);
        if (!ReferenceEquals(FilesListView.SelectedItem, entry)) return;

        if (groups != null)
            ApplyVisibilityGroups(entry, groups);
        else if (ReferenceEquals(_visibilityEntry, entry))
            VisibilityGroupsStatusText.Text = "Visibility groups unavailable for this preview.";
    }

    // ─── Selection reconciliation ─────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderPngButton.IsEnabled = false;
        RenderGifButton.IsEnabled = false;

        if (FilesListView.SelectedItem is not MeshFileEntry entry)
        {
            UpdateDisplaySettingsVisibility(null);
            ResetVisibilityGroups(null);
            _animPanel.Reset("Select a skinned character to scan for animations.");
            if (_preview != null)
                await _preview.ClearAsync();
            if (FilesListView.SelectedItem != null)
                return;
            await ModelViewer.SetLightingModeAsync("day");
            UpdateRenderButtons();
            return;
        }

        UpdateDisplaySettingsVisibility(entry);
        ResetVisibilityGroups(entry);
        _preview ??= new MeshConverterTabPreview(ModelViewer);
        await _preview.InitializeAsync();
        if (!ReferenceEquals(FilesListView.SelectedItem, entry))
            return;
        await ModelViewer.SetLightingModeAsync(
            entry.IsPakWorldzone ? GetSelectedPreviewLightingMode() : "day");
        if (!ReferenceEquals(FilesListView.SelectedItem, entry))
            return;

        if (!entry.IsAnimatableCharacter)
        {
            _animPanel.Reset("Selected file has no skeleton — pick a skinned character to browse animations.");
            await LoadStaticPreviewAsync(
                entry,
                entry.IsPakWorldzone
                    ? GetSelectedPreviewWorldzoneTimeOfDay()
                    : WorldzoneTimeOfDay.All);
            UpdateRenderButtons();
            return;
        }

        // Character path: bring the Animations pane forward, resolve the
        // skeleton + discover animations, then auto-preview the first match.
        PanelSelector.SelectedItem = AnimationsPanelItem;
        var completed = await _animPanel.LoadForCharacterAsync(entry);
        if (!completed || !ReferenceEquals(FilesListView.SelectedItem, entry))
            return;

        var firstMatch = _animPanel.FirstMatch;
        if (firstMatch != null)
        {
            // ListView can transiently retain the previous character's
            // SelectedItem while its observable source is being rebuilt. The
            // old null-only guard then selected nothing and no preview was
            // requested. Reconcile selection explicitly and load the preview
            // here rather than relying on a SelectionChanged side effect.
            if (!await ReconcileAnimationSelectionAsync(entry, firstMatch))
                return;

            await LoadAnimationPreviewAsync(entry, firstMatch);
        }
        else
        {
            // No matching animations — show the mesh statically so the user
            // always sees something.
            await LoadStaticPreviewAsync(entry);
        }

        UpdateRenderButtons();
    }

    private Task<bool> ReconcileAnimationSelectionAsync(
        MeshFileEntry character, AnimationListEntry animation)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void ApplySelection()
        {
            if (!ReferenceEquals(FilesListView.SelectedItem, character) ||
                !ReferenceEquals(_animPanel.Character, character))
            {
                completion.TrySetResult(false);
                return;
            }

            _suppressAnimationSelectionChanged = true;
            try
            {
                AnimationListView.SelectedItem = null;
                AnimationListView.SelectedItem = animation;
                completion.TrySetResult(true);
            }
            finally
            {
                _suppressAnimationSelectionChanged = false;
            }
        }

        // FileTableBehavior rebuilds a sorted projection on the dispatcher.
        // Queue behind that rebuild so the new character's first row exists
        // before assigning SelectedItem.
        if (!DispatcherQueue.TryEnqueue(ApplySelection))
            ApplySelection();
        return completion.Task;
    }

    // ─── Animations pane ──────────────────────────────────────────────────

    private async void AnimationListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressAnimationSelectionChanged)
            return;

        var entry = AnimationListView.SelectedItem as AnimationListEntry;
        var character = _animPanel.Character;
        if (entry == null || character == null || _preview == null)
        {
            ClearActiveAnimation();
            UpdateRenderButtons();
            return;
        }

        await LoadAnimationPreviewAsync(character, entry);
        UpdateRenderButtons();
    }

    private async Task LoadAnimationPreviewAsync(
        MeshFileEntry character,
        AnimationListEntry entry,
        bool preserveCamera = false)
    {
        ClearActiveAnimation();

        if (_preview == null)
            return;

        if (!entry.MatchesSkeleton)
        {
            ModelViewer.SetError(entry.MismatchTooltip);
            return;
        }

        entry.IsActive = true;
        var groups = await _preview.LoadPreviewAsync(
            character,
            entry.Probe,
            GetVisibilityOverridesSnapshot(character),
            preserveCamera);
        if (!ReferenceEquals(FilesListView.SelectedItem, character) ||
            !ReferenceEquals(_animPanel.Character, character))
        {
            return;
        }

        if (groups != null)
            ApplyVisibilityGroups(character, groups);
        else if (ReferenceEquals(_visibilityEntry, character))
            VisibilityGroupsStatusText.Text = "Visibility groups unavailable for this preview.";
    }

    private void ClearActiveAnimation()
    {
        foreach (var anim in _animPanel.Animations)
            if (anim.IsActive)
                anim.IsActive = false;
    }

    private async void AddAnimFolder_Click(object sender, RoutedEventArgs e)
    {
        await _animPanel.AddFolderAsync();
    }

    private async void AddAnimArchive_Click(object sender, RoutedEventArgs e)
    {
        await _animPanel.AddArchiveAsync();
    }

    private void AnimsSelectAll_Click(object sender, RoutedEventArgs e)
    {
        _animPanel.SetAllChecked(true);
    }

    private void AnimsSelectNone_Click(object sender, RoutedEventArgs e)
    {
        _animPanel.SetAllChecked(false);
    }

    private void ShowAllAnims_Click(object sender, RoutedEventArgs e)
    {
        _animPanel.RefreshFilter();
    }

    private async void ConvertGlb_Click(object sender, RoutedEventArgs e)
    {
        var character = _animPanel.Character;
        if (character == null) return;
        await _animExporter.ExportGlbAsync(
            character,
            _animPanel.CheckedMatchingProbes(),
            GetVisibilityOverridesSnapshot(character));
    }

    private async void ConvertBlend_Click(object sender, RoutedEventArgs e)
    {
        var character = _animPanel.Character;
        if (character == null) return;
        await _animExporter.ExportBlendAsync(
            character,
            _animPanel.CheckedMatchingProbes(),
            GetVisibilityOverridesSnapshot(character));
    }

    // ─── Convert (batch over checked files) ───────────────────────────────

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        if (checkedEntries.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one file to convert.");
            return;
        }

        if (!TryGetWorldzoneScale(checkedEntries, out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var outputFormat = GetSelectedOutputFormat();
        var destination = await PickConvertDestinationAsync(checkedEntries, outputFormat);
        if (destination == null) return;
        var visibilityEntry = FilesListView.SelectedItem as MeshFileEntry;

        await _batchRunner.ConvertAsync(
            checkedEntries,
            destination.Value.OutputDirectory,
            GetSelectedExportWorldzoneTimeOfDay(),
            worldzoneScale,
            outputFormat,
            singleOutputStem: destination.Value.OutputStem,
            visibilityEntry: visibilityEntry,
            visibilityOverrides: visibilityEntry == null
                ? null
                : GetVisibilityOverridesSnapshot(visibilityEntry),
            includeLevelObjects: ShouldIncludeLevelObjectsInExport());
    }

    private async Task<(string OutputDirectory, string? OutputStem)?> PickConvertDestinationAsync(
        List<MeshFileEntry> entries,
        MeshOutputFormat outputFormat)
    {
        if (entries.Count > 1)
        {
            var folder = await FolderPickerHelper.PickFolderAsync();
            if (folder == null) return null;
            return (folder, null);
        }

        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entries[0].FileName);
        var path = outputFormat switch
        {
            MeshOutputFormat.Blend => await FilePickerHelper.PickSaveFileAsync(
                stem,
                ("Blender model", [".blend"])),
            MeshOutputFormat.Both => await FilePickerHelper.PickSaveFileAsync(
                stem,
                ("Model files", [".glb", ".blend"])),
            _ => await FilePickerHelper.PickSaveFileAsync(
                stem,
                ("glTF model", [".glb"]))
        };
        if (path == null) return null;

        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(directory))
            return null;

        var outputStem = Path.GetFileNameWithoutExtension(path);
        if (outputFormat == MeshOutputFormat.Both)
        {
            var pickedExtension = Path.GetExtension(path);
            var companionExtension = pickedExtension.Equals(
                ".blend", StringComparison.OrdinalIgnoreCase)
                ? ".glb"
                : ".blend";
            var companionPath = Path.Combine(directory, outputStem + companionExtension);
            if (File.Exists(companionPath) && !await ConfirmCompanionOverwriteAsync(companionPath))
                return null;
        }

        return (directory, outputStem);
    }

    private async Task<bool> ConfirmCompanionOverwriteAsync(string companionPath)
    {
        var dialog = new ContentDialog
        {
            Title = "Replace both model files?",
            Content = $"{Path.GetFileName(companionPath)} already exists beside the file you selected.",
            PrimaryButtonText = "Replace both",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _batchRunner.CancelAsync();
        _animExporter.Cancel();
        MainWindow.Instance?.SetStatus("Operation cancelled");
    }

    private MeshOutputFormat GetSelectedOutputFormat()
    {
        var glb = ExportGlbCheckbox.IsChecked == true;
        var blend = ExportBlendCheckbox.IsChecked == true;
        if (glb && blend) return MeshOutputFormat.Both;
        return blend ? MeshOutputFormat.Blend : MeshOutputFormat.Glb;
    }

    private WorldzoneTimeOfDay GetSelectedPreviewWorldzoneTimeOfDay()
    {
        var tag = (WorldzoneTimeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag == "night" ? WorldzoneTimeOfDay.Night : WorldzoneTimeOfDay.Day;
    }

    private string GetSelectedPreviewLightingMode() =>
        GetSelectedPreviewWorldzoneTimeOfDay() == WorldzoneTimeOfDay.Night
            ? "night"
            : "day";

    private WorldzoneTimeOfDay GetSelectedExportWorldzoneTimeOfDay() =>
        ExportWorldzoneNightToggle.IsOn
            ? WorldzoneTimeOfDay.Night
            : WorldzoneTimeOfDay.Day;

    private bool ShouldIncludeLevelObjectsInExport() =>
        ExportIncludeLevelObjectsCheckbox.IsChecked != false;

    private async void DisplayIncludeLevelObjectsCheckbox_Click(
        object sender,
        RoutedEventArgs e)
    {
        if (FilesListView.SelectedItem is not MeshFileEntry
            {
                HasSupportedLevelObjectCompanion: true
            } entry)
        {
            return;
        }

        await LoadStaticPreviewAsync(
            entry,
            entry.IsPakWorldzone
                ? GetSelectedPreviewWorldzoneTimeOfDay()
                : WorldzoneTimeOfDay.All,
            preserveCamera: true);
        UpdateRenderButtons();
    }

    private async void WorldzoneTimeCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (WorldzoneTimeCombo == null || ModelViewer == null) return;

        var selectedTime = GetSelectedPreviewWorldzoneTimeOfDay();
        if (FilesListView?.SelectedItem is not MeshFileEntry { IsPakWorldzone: true } entry)
        {
            await ModelViewer.SetLightingModeAsync("day");
            return;
        }

        await ModelViewer.SetLightingModeAsync(
            selectedTime == WorldzoneTimeOfDay.Night ? "night" : "day");

        _preview ??= new MeshConverterTabPreview(ModelViewer);
        await _preview.InitializeAsync();
        if (!ReferenceEquals(FilesListView.SelectedItem, entry) ||
            GetSelectedPreviewWorldzoneTimeOfDay() != selectedTime)
            return;

        await LoadStaticPreviewAsync(entry, selectedTime);
        UpdateRenderButtons();
    }

    private bool TryGetWorldzoneScale(out float scale)
    {
        var text = WorldzoneScaleText.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            scale = 1f;
            return true;
        }

        return float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out scale)
               && float.IsFinite(scale)
               && scale > 0f;
    }

    private bool TryGetWorldzoneScale(IEnumerable<MeshFileEntry> entries, out float scale)
    {
        if (!entries.Any(entry => entry.IsPakWorldzone))
        {
            scale = 1f;
            return true;
        }

        return TryGetWorldzoneScale(out scale);
    }

    // ─── Render (single preview or batch over checked files) ─────────────

    private void ModelViewer_ModelLoaded(object? sender, EventArgs e)
    {
        UpdateRenderButtons();
    }

    private void UpdateRenderButtons()
    {
        var hasGlb = ModelViewer.LastGlbBytes != null;
        var checkedCount = _items.Count(i => i.IsChecked);
        RenderPngButton.IsEnabled = hasGlb || checkedCount > 0;
        RenderGifButton.IsEnabled = (hasGlb && ModelViewer.HasAnimations) || checkedCount > 1;
    }

    private async void RenderPng_Click(object sender, RoutedEventArgs e)
    {
        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        var previewGlb = ModelViewer.LastGlbBytes;
        var selectedEntry = FilesListView.SelectedItem as MeshFileEntry;
        var visibilityOverrides = selectedEntry == null
            ? null
            : GetVisibilityOverridesSnapshot(selectedEntry);
        var includeLevelObjects = ShouldIncludeLevelObjectsInExport();
        var selectedNeedsExportRebuild = selectedEntry?.IsPakWorldzone == true
                                         || selectedEntry?.HasSupportedLevelObjectCompanion == true;
        IEnumerable<MeshFileEntry> worldzoneEntries = checkedEntries;
        if (checkedEntries.Count <= 1 && selectedEntry != null)
            worldzoneEntries = [selectedEntry];

        if (!TryGetWorldzoneScale(worldzoneEntries, out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var size = (int)RenderSizeBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;
        var objectReview = ObjectReviewCheckbox.IsChecked == true;

        if (checkedEntries.Count > 1)
        {
            var outputDir = await FolderPickerHelper.PickFolderAsync();
            if (outputDir == null) return;

            await _batchRunner.RenderPngBatchAsync(
                checkedEntries, outputDir, size, azimuth, elevation, objectReview,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale,
                selectedEntry, visibilityOverrides,
                includeLevelObjects: includeLevelObjects);
            return;
        }

        // The object-review preset emits five named views, so it remains a
        // folder operation even though it starts from one input mesh.
        if (objectReview)
        {
            var outputDir = await FolderPickerHelper.PickFolderAsync();
            if (outputDir == null) return;

            if (selectedNeedsExportRebuild && selectedEntry != null)
            {
                await _batchRunner.RenderPngEntryAsync(
                    selectedEntry, outputDir, GetSelectedStem(), size, azimuth, elevation, true,
                    GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale, visibilityOverrides,
                    includeLevelObjects: includeLevelObjects);
            }
            else if (previewGlb != null)
            {
                await _batchRunner.RenderPngSingleAsync(
                    previewGlb, outputDir, GetSelectedStem(), size, azimuth, elevation, true);
            }
            else if (checkedEntries.Count == 1)
            {
                var entry = checkedEntries[0];
                var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
                await _batchRunner.RenderPngEntryAsync(
                    entry, outputDir, stem, size, azimuth, elevation, true,
                    GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale,
                    ReferenceEquals(entry, selectedEntry) ? visibilityOverrides : null,
                    includeLevelObjects: includeLevelObjects);
            }

            return;
        }

        var suggestedStem = "render";
        if (previewGlb != null || selectedNeedsExportRebuild)
            suggestedStem = GetSelectedStem();
        else if (checkedEntries.Count == 1)
            suggestedStem = MeshConverterTabFileScanner.StripCompoundExtension(checkedEntries[0].FileName);
        var outputPath = await FilePickerHelper.PickSaveFileAsync(
            suggestedStem,
            ("PNG image", [".png"]));
        if (outputPath == null) return;

        var singleOutputDir = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(singleOutputDir)) return;
        var outputStem = Path.GetFileNameWithoutExtension(outputPath);

        if (selectedNeedsExportRebuild && selectedEntry != null)
        {
            // Worldzones and companion-backed levels have independent display
            // and export settings, so rebuild instead of reusing the viewport GLB.
            await _batchRunner.RenderPngEntryAsync(
                selectedEntry, singleOutputDir, outputStem, size, azimuth, elevation, false,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale, visibilityOverrides,
                includeLevelObjects: includeLevelObjects);
        }
        else if (previewGlb != null)
        {
            await _batchRunner.RenderPngSingleAsync(
                previewGlb, singleOutputDir, outputStem, size, azimuth, elevation, false);
        }
        else if (checkedEntries.Count == 1)
        {
            await _batchRunner.RenderPngEntryAsync(
                checkedEntries[0], singleOutputDir, outputStem, size, azimuth, elevation, false,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale,
                ReferenceEquals(checkedEntries[0], selectedEntry) ? visibilityOverrides : null,
                includeLevelObjects: includeLevelObjects);
        }
    }

    private async void RenderGif_Click(object sender, RoutedEventArgs e)
    {
        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        var previewGlb = ModelViewer.LastGlbBytes;
        var hasAnimatedPreview = previewGlb != null && ModelViewer.HasAnimations;
        var selectedEntry = FilesListView.SelectedItem as MeshFileEntry;
        var visibilityOverrides = selectedEntry == null
            ? null
            : GetVisibilityOverridesSnapshot(selectedEntry);
        var includeLevelObjects = ShouldIncludeLevelObjectsInExport();
        IEnumerable<MeshFileEntry> worldzoneEntries = checkedEntries;
        if (checkedEntries.Count <= 1 && selectedEntry != null)
            worldzoneEntries = [selectedEntry];

        if (!TryGetWorldzoneScale(worldzoneEntries, out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var size = (int)RenderSizeBox.Value;
        var fps = (int)RenderFpsBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;

        if (hasAnimatedPreview && checkedEntries.Count <= 1)
        {
            var outputPath = await FilePickerHelper.PickSaveFileAsync(
                GetSelectedStem(),
                ("GIF image", [".gif"]));
            if (outputPath == null) return;

            await _batchRunner.RenderGifSingleAsync(
                previewGlb!, outputPath, size, fps, azimuth, elevation);
        }
        else
        {
            var outputDir = await FolderPickerHelper.PickFolderAsync();
            if (outputDir == null) return;

            await _batchRunner.RenderGifBatchAsync(
                checkedEntries, outputDir, size, fps, azimuth, elevation,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale,
                selectedEntry, visibilityOverrides,
                includeLevelObjects: includeLevelObjects);
        }
    }

    /// <summary>
    ///     Output stem for single renders: the selected file, with the active
    ///     animation appended when the loaded preview is an animated character
    ///     (matches the old Character Preview GIF naming).
    /// </summary>
    private string GetSelectedStem()
    {
        if (FilesListView.SelectedItem is not MeshFileEntry entry)
            return "render";

        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName);
        var activeAnim = _animPanel.ActiveEntry;
        if (activeAnim != null && ReferenceEquals(_animPanel.Character, entry))
            stem += "_" + StripAnimExtension(activeAnim.DisplayName);
        return stem;
    }

    private static string StripAnimExtension(string fileName)
    {
        var idx = fileName.IndexOf(".ska", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? fileName[..idx] : Path.GetFileNameWithoutExtension(fileName);
    }

    private void MeshConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
