using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;

namespace NeversoftMultitool;

/// <summary>
///     Merged mesh + character tab: one file list (with batch checkboxes), one
///     3D viewer, and a tabbed right panel (Animations / Settings / Export).
///     Selecting a skinned character resolves its skeleton and populates the
///     Animations pane; everything else previews statically.
/// </summary>
public sealed partial class MeshConverterTab : UserControl, IDisposable
{
    private readonly MeshConverterTabAnimationExporter _animExporter;
    private readonly MeshConverterTabAnimationPanel _animPanel;
    private readonly MeshConverterTabBatchRunner _batchRunner;
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private string _inputDir = "";
    private string _outputDir = "";
    private bool _outputManuallySet;
    private MeshConverterTabPreview? _preview;
    private CancellationTokenSource? _scanCts;
    private bool _suppressAnimationSelectionChanged;

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

        _inputDir = path;
        InputPathText.Text = _inputDir;
        DefaultOutputToInput(path);
        await RunRecursiveScan(path);
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(
            [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr"]);
        if (path == null) return;

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);

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

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        _outputManuallySet = true;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void DefaultOutputToInput(string dir)
    {
        if (_outputManuallySet)
            return;

        _outputDir = dir;
        OutputPathText.Text = dir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasChecked = _items.Any(i => i.IsChecked);
        var hasOutput = !string.IsNullOrEmpty(_outputDir);
        var hasFormat = ExportGlbCheckbox.IsChecked == true || ExportBlendCheckbox.IsChecked == true;

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasChecked && hasOutput && hasFormat;
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

    // ─── Selection reconciliation ─────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        RenderPngButton.IsEnabled = false;
        RenderGifButton.IsEnabled = false;

        if (FilesListView.SelectedItem is not MeshFileEntry entry)
        {
            _animPanel.Reset("Select a skinned character to scan for animations.");
            if (_preview != null)
                await _preview.ClearAsync();
            UpdateRenderButtons();
            return;
        }

        _preview ??= new MeshConverterTabPreview(ModelViewer);
        await _preview.InitializeAsync();
        await ModelViewer.SetLightingModeAsync(GetSelectedPreviewLightingMode());
        if (!ReferenceEquals(FilesListView.SelectedItem, entry))
            return;

        if (!entry.IsAnimatableCharacter)
        {
            _animPanel.Reset("Selected file has no skeleton — pick a skinned character to browse animations.");
            await _preview.LoadPreviewAsync(
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
            await _preview.LoadPreviewAsync(entry);
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
        MeshFileEntry character, AnimationListEntry entry)
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
        await _preview.LoadPreviewAsync(character, entry.Probe);
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
        await _animExporter.ExportGlbAsync(character, _animPanel.CheckedMatchingProbes());
    }

    private async void ConvertBlend_Click(object sender, RoutedEventArgs e)
    {
        var character = _animPanel.Character;
        if (character == null) return;
        await _animExporter.ExportBlendAsync(character, _animPanel.CheckedMatchingProbes());
    }

    // ─── Convert (batch over checked files) ───────────────────────────────

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_outputDir)) return;

        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        if (checkedEntries.Count == 0)
        {
            MainWindow.Instance?.SetStatus("Check at least one file to convert.");
            return;
        }

        if (!TryGetWorldzoneScale(out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        await _batchRunner.ConvertAsync(
            checkedEntries,
            _outputDir,
            GetSelectedExportWorldzoneTimeOfDay(),
            worldzoneScale,
            GetSelectedOutputFormat());
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

    private async void WorldzoneTimeCombo_SelectionChanged(
        object sender, SelectionChangedEventArgs e)
    {
        if (WorldzoneTimeCombo == null || ModelViewer == null) return;

        var selectedTime = GetSelectedPreviewWorldzoneTimeOfDay();
        await ModelViewer.SetLightingModeAsync(
            selectedTime == WorldzoneTimeOfDay.Night ? "night" : "day");
        if (FilesListView?.SelectedItem is not MeshFileEntry { IsPakWorldzone: true } entry)
            return;

        _preview ??= new MeshConverterTabPreview(ModelViewer);
        await _preview.InitializeAsync();
        if (!ReferenceEquals(FilesListView.SelectedItem, entry) ||
            GetSelectedPreviewWorldzoneTimeOfDay() != selectedTime)
            return;

        await _preview.LoadPreviewAsync(entry, selectedTime);
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
        if (!TryGetWorldzoneScale(out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        var previewGlb = ModelViewer.LastGlbBytes;

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var size = (int)RenderSizeBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;
        var objectReview = ObjectReviewCheckbox.IsChecked == true;

        if (checkedEntries.Count > 1)
        {
            await _batchRunner.RenderPngBatchAsync(
                checkedEntries, outputDir, size, azimuth, elevation, objectReview,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale);
        }
        else if (FilesListView.SelectedItem is MeshFileEntry { IsPakWorldzone: true } selectedWorldzone)
        {
            // A worldzone's preview uses the viewport lighting choice. Rebuild it
            // for export so the independent export lighting and scale controls
            // also apply when rendering just the selected worldzone.
            await _batchRunner.RenderPngBatchAsync(
                new[] { selectedWorldzone }, outputDir, size, azimuth, elevation, objectReview,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale);
        }
        else if (previewGlb != null)
        {
            await _batchRunner.RenderPngSingleAsync(
                previewGlb, outputDir, GetSelectedStem(), size, azimuth, elevation, objectReview);
        }
        else if (checkedEntries.Count == 1)
        {
            await _batchRunner.RenderPngBatchAsync(
                checkedEntries, outputDir, size, azimuth, elevation, objectReview,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale);
        }
    }

    private async void RenderGif_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetWorldzoneScale(out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        var previewGlb = ModelViewer.LastGlbBytes;
        var hasAnimatedPreview = previewGlb != null && ModelViewer.HasAnimations;

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        var size = (int)RenderSizeBox.Value;
        var fps = (int)RenderFpsBox.Value;
        var azimuth = (float)RenderAzimuthBox.Value;
        var elevation = (float)RenderElevationBox.Value;

        if (hasAnimatedPreview && checkedEntries.Count <= 1)
        {
            var outputPath = Path.Combine(outputDir, GetSelectedStem() + ".gif");
            await _batchRunner.RenderGifSingleAsync(
                previewGlb!, outputPath, size, fps, azimuth, elevation);
        }
        else
        {
            await _batchRunner.RenderGifBatchAsync(
                checkedEntries, outputDir, size, fps, azimuth, elevation,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale);
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
