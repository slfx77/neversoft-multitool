using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core.BinaryIO;
using NeversoftMultitool.Core.Formats.Gba;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene;
using NeversoftMultitool.Core.Rendering;
using NeversoftMultitool.Core.Rendering.Level2d;

namespace NeversoftMultitool;

/// <summary>
///     World-scale content — level geometry, object banks and worldzones — split
///     out of Meshes &amp; Characters.
/// </summary>
/// <remarks>
///     Levels want a different tool than characters do: a first-person camera, the
///     level's own display layers, and an export that reproduces a view rather than
///     a turntable. They shared a tab with an Animations pane and a skeleton picker
///     that never applied to them.
///     <para>
///         Scanning is shared through <see cref="MeshTabScanSession" /> — the tabs
///         render complementary slices of one scan, so a level can never appear in
///         both lists bound to the same mutable row.
///     </para>
/// </remarks>
public sealed partial class LevelsTab : UserControl, IDisposable
{
    private const MeshScanSlice Slice = MeshScanSlice.Levels;

    private readonly MeshConverterTabBatchRunner _batchRunner;
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private readonly MeshTabScanSession _scan = MeshTabScanSession.Instance;

    private readonly Dictionary<string, bool> _visibilityOverrides = new(StringComparer.Ordinal);

    private DebouncedAction? _filesFilterDebounce;

    /// <summary>The 2D source for the selected level, or null when it has no picture.</summary>
    private ILevel2dSource? _level2d;

    private Level2dLayer _level2dLayer = Level2dLayer.Art;

    /// <summary>Set while the code is driving the pickers, so their handlers stand down.</summary>
    private bool _applyingViewMode;

    /// <summary>Set while the projection change is refilling the direction picker.</summary>
    private bool _refillingDirections;

    /// <summary>The user's explicit choice, once made; null means "use the level's default".</summary>
    private bool? _userChoseTwoDimensional;
    private string _filesFilterText = "";
    private MeshConverterTabPreview? _preview;
    private CancellationTokenSource? _scanCts;
    private MeshFileEntry? _visibilityEntry;

    public LevelsTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;

        _filesFilterDebounce = new DebouncedAction(
            DispatcherQueue, TimeSpan.FromMilliseconds(400), ApplyFilesFilter);

        _batchRunner = new MeshConverterTabBatchRunner(
            ConversionProgress, ConvertButton, CancelButton, DispatcherQueue);

        _scan.Changed += OnScanSessionChanged;
        Unloaded += LevelsTab_Unloaded;

        // Selected here rather than in XAML so SelectionChanged can't fire
        // mid-InitializeComponent against not-yet-created elements.
        PanelSelector.SelectedItem = ExportPanelItem;
        _applyingViewMode = true;
        ViewModeSelector.SelectedItem = View3dItem;
        _applyingViewMode = false;
        InitialiseProjectionPickers();

        // Kept alive by their Click subscriptions on the panel buttons.
        _ = new PanelCollapseController(
            FileListColumn, LeftSplitter, LeftPanelContent, LeftCollapsedStrip,
            CollapseLeftPanelButton, ExpandLeftPanelButton);
        _ = new PanelCollapseController(
            SidePanelColumn, RightSplitter, RightPanelContent, RightCollapsedStrip,
            CollapseRightPanelButton, ExpandRightPanelButton);

        // Kept alive by its SizeChanged/width-callback subscriptions.
        _ = new PanelWidthClamp(
            ContentColumnsGrid, FileListColumn, ViewerColumn, SidePanelColumn);

        OnScanSessionChanged();
    }

    public void Dispose()
    {
        _scan.Changed -= OnScanSessionChanged;
        Unloaded -= LevelsTab_Unloaded;
        _scanCts?.Dispose();
        _scanCts = null;
        _preview?.Dispose();
        _preview = null;
        _batchRunner.Dispose();
        _filesFilterDebounce = null;
    }

    private void LevelsTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }

    private MeshConverterTabPreview Preview => _preview ??= new MeshConverterTabPreview(ModelViewer);

    // ---- Scanning ----

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
            [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr", ".z64", ".gba", ".nds", ".gob"]);
        if (path == null) return;

        InputPathText.Text = path;
        await CancelInFlightScan();
        _scan.Clear();

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        try
        {
            MainWindow.Instance?.SetStatus($"Scanning {Path.GetFileName(path)}...");
            var progress = new Progress<int>(count =>
                MainWindow.Instance?.SetStatus(
                    $"Scanning {Path.GetFileName(path)}: {count} entries probed..."));

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanArchive(path, progress, token), token);
            token.ThrowIfCancellationRequested();

            _scan.Publish(path, entries);
            ReportScanResult(Path.GetFileName(path));
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
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    private async Task RunRecursiveScan(string rootDir)
    {
        await CancelInFlightScan();
        _scan.Clear();

        var cts = new CancellationTokenSource();
        _scanCts = cts;
        var token = cts.Token;

        MainWindow.Instance?.SetStatus("Scanning directory...");

        try
        {
            var scanSummary = await Task.Run(
                () => MeshConverterTabFileScanner.AnalyzeDirectory(rootDir, token), token);

            if (scanSummary.UnsupportedFiles.Count > 0)
            {
                var proceed = await ScanSummaryDialog.ShowIfNeeded(
                    XamlRoot, scanSummary.SupportedCount, [.. scanSummary.UnsupportedFiles]);
                if (!proceed)
                {
                    MainWindow.Instance?.SetStatus("Scan cancelled");
                    return;
                }
            }

            var progress = new Progress<int>(count =>
                MainWindow.Instance?.SetStatus($"Scanning: {count} files probed..."));

            var entries = await Task.Run(
                () => MeshConverterTabFileScanner.ScanDirectory(rootDir, progress, token), token);
            token.ThrowIfCancellationRequested();

            _scan.Publish(rootDir, entries);
            ReportScanResult(null);
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
            if (ReferenceEquals(_scanCts, cts)) _scanCts = null;
            cts.Dispose();
            UpdateUiState();
        }
    }

    /// <summary>
    ///     A scan finding only characters is a normal outcome here, not a failure —
    ///     say where they went rather than reporting nothing found.
    /// </summary>
    private void ReportScanResult(string? archiveName)
    {
        var levels = _scan.CountIn(MeshScanSlice.Levels);
        var models = _scan.CountIn(MeshScanSlice.Models);
        var where = archiveName == null ? "" : $" in {archiveName}";

        MainWindow.Instance?.SetStatus(levels switch
        {
            0 when models == 0 => $"No supported mesh entries found{where}.",
            0 => $"No levels{where} — {models:N0} model(s) are in Meshes & Characters.",
            _ when models == 0 => $"Found {levels:N0} level(s){where}.",
            _ => $"Found {levels:N0} level(s){where}; {models:N0} model(s) are in Meshes & Characters."
        });
    }

    private async Task CancelInFlightScan()
    {
        var existing = _scanCts;
        if (existing == null) return;
        _scanCts = null;
        await existing.CancelAsync();
    }

    private void OnScanSessionChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnScanSessionChanged);
            return;
        }

        _scan.FillSlice(Slice, _items);
        if (InputPathText != null && _scan.SourcePath.Length > 0)
            InputPathText.Text = _scan.SourcePath;
        UpdateUiState();
    }

    private void GoToModels_Click(object sender, RoutedEventArgs e)
    {
        var models = _scan.CountIn(MeshScanSlice.Models);
        MainWindow.Instance?.SelectTab("MeshConverter");
        // After the jump: NavView_SelectionChanged clears the status bar.
        MainWindow.Instance?.SetStatus($"{models:N0} model(s) from the last scan.");
    }

    // ---- List state ----

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var checkedCount = _items.Count(i => i.IsChecked);

        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;

        var models = _scan.CountIn(MeshScanSlice.Models);
        if (!hasFiles && models > 0)
        {
            EmptyStateTitle.Text = "No levels in this scan";
            EmptyStateDetail.Text =
                $"The last scan found {models:N0} model(s) but no world-scale content. "
                + "Characters, props and collision meshes open in Meshes & Characters.";
            GoToModelsButton.Visibility = Visibility.Visible;
        }
        else if (!hasFiles)
        {
            EmptyStateTitle.Text = "No levels loaded";
            EmptyStateDetail.Text =
                "Select a folder (scanned recursively) or a game archive. Level geometry, "
                + "object banks and their companions are resolved automatically; characters "
                + "and props open in Meshes & Characters.";
            GoToModelsButton.Visibility = Visibility.Collapsed;
        }

        ConvertButton.IsEnabled = checkedCount > 0 && SelectedOutputFormat() != null;
        UpdateWorldzoneExportSettingsVisibility();
        UpdateLevelObjectExportSettingsVisibility();
        UpdateOutputSizeText();
        UpdateRenderButtons();
    }

    /// <summary>The chosen output format, or null when neither box is ticked.</summary>
    private MeshOutputFormat? SelectedOutputFormat()
    {
        var glb = ExportGlbCheckbox.IsChecked == true;
        var blend = ExportBlendCheckbox.IsChecked == true;
        return (glb, blend) switch
        {
            (true, true) => MeshOutputFormat.Both,
            (true, false) => MeshOutputFormat.Glb,
            (false, true) => MeshOutputFormat.Blend,
            _ => null
        };
    }

    private void UpdateRenderButtons()
    {
        if (ShowingTwoDimensional)
        {
            RenderPngButton.IsEnabled = _level2d != null;
            RenderPngButton.Content = "Export image as PNG...";
            RenderSettingsPanel.Visibility = Visibility.Collapsed;
            return;
        }

        var hasGlb = ModelViewer.LastGlbBytes is { Length: > 0 };
        var checkedCount = _items.Count(i => i.IsChecked);
        RenderPngButton.IsEnabled = hasGlb || checkedCount > 0;
        RenderPngButton.Content = "Export render as PNG...";
        RenderSettingsPanel.Visibility = Visibility.Visible;
        UpdateOutputSizeText();
    }

    /// <summary>
    ///     Export settings apply to checked rows, which need not be the row
    ///     currently selected for preview. Keep the viewport control tied to
    ///     selection, but expose export lighting/scale whenever the operation
    ///     scope contains an actual content-detected worldzone.
    /// </summary>
    private void UpdateWorldzoneExportSettingsVisibility()
    {
        if (WorldzoneExportSettingsSection == null) return;

        WorldzoneExportSettingsSection.Visibility = _items.Any(static entry =>
            entry.IsChecked && entry.IsPakWorldzone)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void UpdateLevelObjectExportSettingsVisibility()
    {
        if (ExportIncludeLevelObjectsCheckbox == null) return;

        ExportIncludeLevelObjectsCheckbox.Visibility = _items.Any(static entry =>
            entry.IsChecked && entry.HasSupportedLevelObjectCompanion)
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
    }

    private WorldzoneTimeOfDay GetSelectedPreviewWorldzoneTimeOfDay()
    {
        var tag = (WorldzoneTimeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString();
        return tag == "night" ? WorldzoneTimeOfDay.Night : WorldzoneTimeOfDay.Day;
    }

    private string GetSelectedPreviewLightingMode() =>
        GetSelectedPreviewWorldzoneTimeOfDay() == WorldzoneTimeOfDay.Night ? "night" : "day";

    private WorldzoneTimeOfDay GetSelectedExportWorldzoneTimeOfDay() =>
        ExportWorldzoneNightToggle.IsOn ? WorldzoneTimeOfDay.Night : WorldzoneTimeOfDay.Day;

    private bool ShouldIncludeLevelObjectsInExport() =>
        ExportIncludeLevelObjectsCheckbox.IsChecked != false;

    private bool ShouldIncludeLevelObjectsInPreview() =>
        DisplayIncludeLevelObjectsCheckbox.IsChecked != false;

    /// <summary>The preview time of day for one entry: only a worldzone has one.</summary>
    private WorldzoneTimeOfDay PreviewTimeFor(MeshFileEntry entry) =>
        entry.IsPakWorldzone ? GetSelectedPreviewWorldzoneTimeOfDay() : WorldzoneTimeOfDay.All;

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

    private async void DisplayIncludeLevelObjectsCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (FilesListView.SelectedItem is not MeshFileEntry
            {
                HasSupportedLevelObjectCompanion: true
            } entry)
        {
            return;
        }

        await ReloadPreviewAsync(entry, preserveCamera: true);
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

        await Preview.InitializeAsync();
        // A newer selection or another lighting change may have landed while the
        // viewer round-tripped; that request owns the preview, not this one.
        if (!ReferenceEquals(FilesListView.SelectedItem, entry) ||
            GetSelectedPreviewWorldzoneTimeOfDay() != selectedTime)
            return;

        await ReloadPreviewAsync(entry, preserveCamera: false);
    }

    private bool ShowingTwoDimensional =>
        ReferenceEquals(ViewModeSelector.SelectedItem, View2dItem);

    private void ViewModeSelector_SelectionChanged(
        SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_applyingViewMode || ImageViewer == null || ModelViewer == null) return;
        _userChoseTwoDimensional = ShowingTwoDimensional;
        ApplyViewMode();
    }

    /// <summary>
    ///     Show the surface the current mode selects and hide the other.
    /// </summary>
    /// <remarks>
    ///     Focus moves with the surface: the 3D viewer owns WASD, F and P, and
    ///     leaving focus on a hidden WebView sends those keys nowhere the user can see.
    /// </remarks>
    private void ApplyViewMode()
    {
        var twoD = ShowingTwoDimensional;
        LayerCombo.Visibility = twoD ? Visibility.Visible : Visibility.Collapsed;
        ImageViewer.Visibility = twoD ? Visibility.Visible : Visibility.Collapsed;
        ModelViewer.Visibility = twoD ? Visibility.Collapsed : Visibility.Visible;

        if (twoD)
        {
            RenderTwoDimensional();
            ImageViewer.Focus(FocusState.Programmatic);
        }
        else
        {
            ModelViewer.Focus(FocusState.Programmatic);
        }

        UpdateRenderButtons();
    }

    /// <summary>
    ///     Point the 2D surface at one level, and choose the opening mode.
    /// </summary>
    /// <remarks>
    ///     The default is per format — a GBA level opens on the picture it was
    ///     authored as — but the user's explicit choice wins for the rest of the
    ///     session. It is deliberately NOT persisted: it is a way of looking at one
    ///     level, not a preference about the app.
    /// </remarks>
    private void BindTwoDimensionalSource(MeshFileEntry? entry)
    {
        _level2d = TryCreateLevel2dSource(entry);

        _applyingViewMode = true;
        LayerCombo.Items.Clear();
        if (_level2d != null)
        {
            foreach (var layer in _level2d.Layers)
            {
                LayerCombo.Items.Add(new ComboBoxItem
                {
                    Content = Level2dViewPolicy.LayerLabel(layer),
                    Tag = layer
                });
            }

            var index = _level2d.Layers.ToList().IndexOf(_level2dLayer);
            LayerCombo.SelectedIndex = index >= 0 ? index : 0;
            _level2dLayer = SelectedLayer();
        }

        var wantTwoD = _level2d != null
                       && (_userChoseTwoDimensional
                           ?? (entry != null && Level2dViewPolicy.DefaultsToTwoDimensional(entry.FileName)));
        ViewModeSelector.SelectedItem = wantTwoD ? View2dItem : View3dItem;
        View2dItem.IsEnabled = _level2d != null;
        _applyingViewMode = false;

        ApplyViewMode();
    }

    private static ILevel2dSource? TryCreateLevel2dSource(MeshFileEntry? entry)
    {
        if (entry == null || !Level2dViewPolicy.Supports(entry.FileName)) return null;

        try
        {
            var rom = entry.Source.TryReadCompanion(GbaLevelCarver.RomEntryName);
            return rom == null
                ? null
                : GbaLevel2dSource.TryCreate(entry.Source.ReadBytes(), rom, entry.FileName);
        }
        catch (Exception)
        {
            // A picture is an extra, never a reason to fail opening the level.
            return null;
        }
    }

    private Level2dLayer SelectedLayer() =>
        (LayerCombo.SelectedItem as ComboBoxItem)?.Tag is Level2dLayer layer ? layer : Level2dLayer.Art;

    private void LayerCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingViewMode || _level2d == null) return;
        _level2dLayer = SelectedLayer();
        RenderTwoDimensional();
        UpdateRenderButtons();
    }

    private void RenderTwoDimensional()
    {
        if (_level2d == null)
        {
            ImageViewer.Clear();
            return;
        }

        var render = _level2d.Render(_level2dLayer);
        if (render == null)
        {
            ImageViewer.Clear();
            MainWindow.Instance?.SetStatus(
                $"{_level2d.DisplayName}: no {Level2dViewPolicy.LayerLabel(_level2dLayer)} to show.");
            return;
        }

        ImageViewer.SetSource(
            BitmapHelper.CreateFromRgba(render.Value.Width, render.Value.Height, render.Value.Rgba));
        MainWindow.Instance?.SetStatus(
            $"{_level2d.DisplayName} - {Level2dViewPolicy.LayerLabel(_level2dLayer)} "
            + $"{render.Value.Width} x {render.Value.Height} px");
    }

    /// <summary>
    ///     Fill the projection picker and select isometric — a straight-down view
    ///     flattens every wall to nothing, which makes one building look like the next.
    /// </summary>
    private void InitialiseProjectionPickers()
    {
        _refillingDirections = true;
        foreach (var projection in Enum.GetValues<LevelExportProjection>())
        {
            ProjectionCombo.Items.Add(new ComboBoxItem
            {
                Content = LevelExportProjections.Label(projection),
                Tag = projection
            });
        }

        ProjectionCombo.SelectedIndex = (int)LevelExportProjection.Isometric;
        _refillingDirections = false;
        RefillDirections();
    }

    private LevelExportProjection SelectedProjection =>
        (ProjectionCombo.SelectedItem as ComboBoxItem)?.Tag is LevelExportProjection p
            ? p
            : LevelExportProjection.Isometric;

    private LevelExportView SelectedDirection =>
        (DirectionCombo.SelectedItem as ComboBoxItem)?.Tag is LevelExportView v
            ? v
            : LevelExportProjections.Directions(SelectedProjection)[0];

    /// <summary>
    ///     The direction names change with the projection ("North at top" only means
    ///     something looking straight down), so the picker is rebuilt per projection.
    /// </summary>
    private void RefillDirections()
    {
        _refillingDirections = true;
        DirectionCombo.Items.Clear();
        foreach (var view in LevelExportProjections.Directions(SelectedProjection))
            DirectionCombo.Items.Add(new ComboBoxItem { Content = view.Label, Tag = view });
        DirectionCombo.SelectedIndex = 0;
        _refillingDirections = false;
        UpdateOutputSizeText();
    }

    private void ProjectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refillingDirections || DirectionCombo == null) return;
        RefillDirections();
    }

    private void DirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refillingDirections) return;
        UpdateOutputSizeText();
    }

    private void RenderSizeBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (OutputSizeText == null) return;
        UpdateOutputSizeText();
    }

    /// <summary>
    ///     Say what the render will actually be, recomputed on every change, so the
    ///     settings are not a guess. Degrades to a sentence rather than blanking.
    /// </summary>
    private void UpdateOutputSizeText()
    {
        if (OutputSizeText == null) return;

        if (FilesListView?.SelectedItem is not MeshFileEntry)
        {
            OutputSizeText.Text = "Select a level to render.";
            return;
        }

        var view = SelectedDirection;
        var edge = (int)Math.Clamp(RenderSizeBox.Value, 64, LevelExportProjections.MaxLongEdge);
        OutputSizeText.Text =
            $"Output: {edge} px on the long edge, framed to the whole level "
            + $"(azimuth {view.Azimuth:0.#}, elevation {view.Elevation:0.##}).";
    }

    private void ExportFormatCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        if (ConvertButton == null) return;
        UpdateUiState();
    }

    private void FileCheckbox_Click(object sender, RoutedEventArgs e) => UpdateUiState();

    private void FilesSelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            if (MatchesCurrentFilter(item))
                item.IsChecked = true;
        UpdateUiState();
    }

    private void FilesSelectNone_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
            if (MatchesCurrentFilter(item))
                item.IsChecked = false;
        UpdateUiState();
    }

    private void FilesFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (FilesFilterBox == null) return;
        _filesFilterDebounce?.Invoke();
    }

    private bool MatchesCurrentFilter(MeshFileEntry entry) =>
        MeshGuiFileFilterPolicy.Matches(entry.RelativePath, entry.FileName, _filesFilterText);

    private void ApplyFilesFilter()
    {
        _filesFilterText = FilesFilterBox.Text?.Trim() ?? "";
        FileTableBehavior.SetRowFilter(
            FilesTableHost,
            _filesFilterText.Length == 0
                ? null
                : item => item is MeshFileEntry entry && MatchesCurrentFilter(entry));
        UpdateUiState();
    }

    private void PanelSelector_SelectionChanged(
        SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (DisplayPane == null || ExportPane == null) return;
        var display = ReferenceEquals(PanelSelector.SelectedItem, DisplayPanelItem);
        DisplayPane.Visibility = display ? Visibility.Visible : Visibility.Collapsed;
        ExportPane.Visibility = display ? Visibility.Collapsed : Visibility.Visible;
    }

    // ---- Preview ----

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var entry = FilesListView.SelectedItem as MeshFileEntry;
        if (!ReferenceEquals(entry, _visibilityEntry))
        {
            _visibilityEntry = entry;
            _visibilityOverrides.Clear();
        }

        if (entry == null)
        {
            UpdateDisplaySettingsVisibility(null);
            BindTwoDimensionalSource(null);
            await ModelViewer.SetLightingModeAsync("day");
            await Preview.ClearAsync();
            UpdateRenderButtons();
            return;
        }

        UpdateDisplaySettingsVisibility(entry);
        BindTwoDimensionalSource(entry);
        await ModelViewer.SetLightingModeAsync(
            entry.IsPakWorldzone ? GetSelectedPreviewLightingMode() : "day");
        await Preview.InitializeAsync();
        await ReloadPreviewAsync(entry, preserveCamera: false);
    }

    /// <summary>
    ///     Rebuild the preview for one entry with the current worldzone lighting
    ///     and level-object settings, then refresh the visibility list from it.
    /// </summary>
    private async Task ReloadPreviewAsync(MeshFileEntry entry, bool preserveCamera)
    {
        var groups = await Preview.LoadPreviewAsync(
            entry,
            PreviewTimeFor(entry),
            _visibilityOverrides.Count == 0 ? null : _visibilityOverrides,
            preserveCamera,
            ShouldIncludeLevelObjectsInPreview());
        if (groups != null) PopulateVisibilityGroups(groups);
        UpdateRenderButtons();
    }

    private void PopulateVisibilityGroups(IReadOnlyList<ModelVisibilityGroup>? groups)
    {
        VisibilityGroupsList.ItemsSource = null;
        var hasGroups = groups is { Count: > 0 };
        DisplayEmptyText.Visibility = hasGroups ? Visibility.Collapsed : Visibility.Visible;
        if (!hasGroups) return;

        var checkboxes = new List<CheckBox>();
        foreach (var group in groups!)
        {
            var box = new CheckBox
            {
                Content = group.Label,
                IsChecked = _visibilityOverrides.TryGetValue(group.Id, out var on)
                    ? on
                    : group.DefaultEnabled,
                Tag = group.Id
            };
            box.Click += VisibilityGroupCheckbox_Click;
            checkboxes.Add(box);
        }

        VisibilityGroupsList.ItemsSource = checkboxes;
    }

    private async void VisibilityGroupCheckbox_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: string key } box || _visibilityEntry == null) return;
        _visibilityOverrides[key] = box.IsChecked == true;

        await ReloadPreviewAsync(_visibilityEntry, preserveCamera: true);
    }

    // ---- Export ----

    private async void Convert_Click(object sender, RoutedEventArgs e)
    {
        var entries = _items.Where(i => i.IsChecked).ToList();
        if (entries.Count == 0) return;

        var format = SelectedOutputFormat();
        if (format == null) return;

        if (!TryGetWorldzoneScale(entries, out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        var outputDir = await FolderPickerHelper.PickFolderAsync();
        if (outputDir == null) return;

        await _batchRunner.ConvertAsync(
            entries, outputDir, GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale, format.Value,
            visibilityEntry: _visibilityEntry,
            visibilityOverrides: _visibilityOverrides.Count == 0 ? null : _visibilityOverrides,
            includeLevelObjects: ShouldIncludeLevelObjectsInExport());
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        await _batchRunner.CancelAsync();
    }

    private async void RenderPng_Click(object sender, RoutedEventArgs e)
    {
        if (ShowingTwoDimensional)
        {
            await ExportTwoDimensionalAsync();
            return;
        }

        var view = SelectedDirection;
        var size = (int)Math.Clamp(RenderSizeBox.Value, 64, LevelExportProjections.MaxLongEdge);
        var azimuth = view.Azimuth;
        var elevation = view.Elevation;
        var checkedEntries = _items.Where(i => i.IsChecked).ToList();
        var selected = FilesListView.SelectedItem as MeshFileEntry;
        var overrides = _visibilityOverrides.Count == 0 ? null : _visibilityOverrides;
        var includeLevelObjects = ShouldIncludeLevelObjectsInExport();

        IEnumerable<MeshFileEntry> scope = checkedEntries;
        if (checkedEntries.Count <= 1 && selected != null) scope = [selected];
        if (!TryGetWorldzoneScale(scope, out var worldzoneScale))
        {
            MainWindow.Instance?.SetStatus("Worldzone scale must be a positive number.");
            return;
        }

        if (checkedEntries.Count > 1)
        {
            var outputDir = await FolderPickerHelper.PickFolderAsync();
            if (outputDir == null) return;

            await _batchRunner.RenderPngBatchAsync(
                checkedEntries, outputDir, size, azimuth, elevation, false,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale, selected, overrides,
                includeLevelObjects: includeLevelObjects);
            return;
        }

        var entry = selected ?? checkedEntries.FirstOrDefault();
        if (entry == null) return;

        var stem = MeshConverterTabFileScanner.StripCompoundExtension(entry.FileName)
                   + SelectedDirection.StemSuffix;
        var target = await FilePickerHelper.PickSaveFileAsync(stem, ("PNG image", [".png"]));
        if (target == null) return;

        var dir = Path.GetDirectoryName(target) ?? ".";
        var saveStem = Path.GetFileNameWithoutExtension(target);

        // A worldzone or level-object companion has to be rebuilt with export
        // settings rather than reusing the preview GLB.
        if (MeshGuiRenderPolicy.RequiresEntryRebuild(
                entry.IsPakWorldzone, entry.HasSupportedLevelObjectCompanion,
                entry.SupportsExplicitXbxSkeleton)
            || ModelViewer.LastGlbBytes is not { Length: > 0 } glb)
        {
            await _batchRunner.RenderPngEntryAsync(
                entry, dir, saveStem, size, azimuth, elevation, false,
                GetSelectedExportWorldzoneTimeOfDay(), worldzoneScale, overrides,
                includeLevelObjects: includeLevelObjects);
            return;
        }

        await _batchRunner.RenderPngSingleAsync(glb, dir, saveStem, size, azimuth, elevation, false);
    }

    /// <summary>
    ///     Write the 2D layer currently on screen, at its native resolution.
    /// </summary>
    /// <remarks>
    ///     No scaling: the art has one true size and upscaling it would invent
    ///     detail the cartridge never had. The checkerboard the preview draws behind
    ///     transparency is a viewing aid and is not baked in.
    /// </remarks>
    private async Task ExportTwoDimensionalAsync()
    {
        if (_level2d == null) return;

        var render = _level2d.Render(_level2dLayer);
        if (render == null)
        {
            MainWindow.Instance?.SetStatus("Nothing to export in this view.");
            return;
        }

        var stem = MeshConverterTabFileScanner.StripCompoundExtension(
                       FilesListView.SelectedItem is MeshFileEntry entry
                           ? entry.FileName
                           : _level2d.DisplayName)
                   + Level2dViewPolicy.LayerStemSuffix(_level2dLayer);

        var target = await FilePickerHelper.PickSaveFileAsync(stem, ("PNG image", [".png"]));
        if (target == null) return;

        try
        {
            var image = render.Value;
            await Task.Run(() => ImageWriter.WritePng(target, image.Width, image.Height, image.Rgba));
            MainWindow.Instance?.SetStatus(
                $"Wrote {image.Width} x {image.Height} px -> {Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            MainWindow.Instance?.SetStatus($"Export failed: {ex.Message}");
        }
    }
}
