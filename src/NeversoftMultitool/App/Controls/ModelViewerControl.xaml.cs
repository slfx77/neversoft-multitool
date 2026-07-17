using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.Web.WebView2.Core;

namespace NeversoftMultitool;

/// <summary>
///     Shared 3D model viewer: WebView2 hosting model-viewer (mesh-viewer.html)
///     plus an animation transport (play/pause/seek). Animated GLBs autoplay
///     their first clip; the transport hides for static models.
/// </summary>
public sealed partial class ModelViewerControl : UserControl
{
    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";

    private string _controlMode = "orbit";
    private double _duration;
    private bool _isPlaying;
    private DateTime _suppressTimeUntil = DateTime.MinValue;
    private bool _updatingSlider;
    private bool _webViewInitialized;

    public ModelViewerControl()
    {
        InitializeComponent();
        // WebView2 renders through its own visual tree and ignores ancestor
        // rounded corners; clip it to match the card it sits in.
        RoundedClipHelper.Apply(ModelWebView, 8);
        // Selected here rather than in XAML so SelectionChanged can't fire
        // mid-InitializeComponent against not-yet-created elements.
        ProjectionCombo.SelectedIndex = (int)ViewerProjection.Perspective;
        UpdateModeUi("orbit");
    }

    /// <summary>The most recently loaded GLB, for render/export reuse.</summary>
    public byte[]? LastGlbBytes { get; private set; }

    /// <summary>Whether the loaded GLB contains animation clips.</summary>
    public bool HasAnimations { get; private set; }

    /// <summary>Raised (on the UI thread) once a loaded model reports its animations.</summary>
    public event EventHandler? ModelLoaded;

    public async Task InitializeAsync()
    {
        if (_webViewInitialized) return;

        try
        {
            await ModelWebView.EnsureCoreWebView2Async();

            // Fly/walk use Ctrl as a slow-move modifier; without this,
            // Ctrl+W(=close)/Ctrl+F(=find) reach the browser instead of the page.
            ModelWebView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            ModelWebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "model-viewer-assets",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            ModelWebView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
            ModelWebView.CoreWebView2.Navigate(
                new UriBuilder(Uri.UriSchemeHttps, "model-viewer-assets")
                    { Path = "mesh-viewer.html" }.Uri.ToString());
            _webViewInitialized = true;

            // The page's own status overlay takes over from here.
            PlaceholderText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            SetError($"WebView2 init failed: {ex.Message}");
        }
    }

    /// <summary>
    ///     Load a GLB into the viewer. Animated models start playing.
    ///     <paramref name="isLevel" /> marks level geometry: levels default to
    ///     the first-person Fly mode (F toggles Fly/Walk), everything else to Orbit.
    /// </summary>
    public async Task LoadGlbAsync(
        byte[] glbBytes,
        bool isLevel = false,
        double? walkEyeHeight = null)
    {
        LastGlbBytes = glbBytes;
        HasAnimations = false;
        HideTransport();

        if (!_webViewInitialized) return;

        // Base64 of a multi-MB GLB is CPU-bound — keep it off the UI thread.
        var base64 = await Task.Run(() => Convert.ToBase64String(glbBytes));
        var eyeHeightScript = walkEyeHeight is > 0 && double.IsFinite(walkEyeHeight.Value)
            ? JsonSerializer.Serialize(walkEyeHeight.Value)
            : "null";
        await ExecuteScriptSafeAsync(
            $"loadModel('{base64}', {(isLevel ? "true" : "false")}, {eyeHeightScript})");
    }

    public async Task ClearAsync()
    {
        LastGlbBytes = null;
        HasAnimations = false;
        HideTransport();
        InfoText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
        SetLoading(false);
        await ExecuteScriptSafeAsync("clearModel()");
    }

    /// <summary>Show a status message inside the 3D viewport overlay.</summary>
    public Task SetViewerStatusAsync(string text)
    {
        return ExecuteScriptSafeAsync($"setStatus('{EscapeJsString(text)}')");
    }

    public void SetInfo(string text)
    {
        InfoText.Text = text;
    }

    public void SetError(string? message)
    {
        if (string.IsNullOrEmpty(message))
        {
            ErrorText.Visibility = Visibility.Collapsed;
            return;
        }

        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    public void SetLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─── Camera presets ───────────────────────────────────────────────────

    /// <summary>Switch projection (true orthographic for Ortho/Iso/Trimetric).</summary>
    public Task SetProjectionAsync(ViewerProjection projection)
    {
        return ExecuteScriptSafeAsync($"setProjection('{ViewerCameraPresets.ScriptName(projection)}')");
    }

    /// <summary>Point the camera at the model from the given orbit angles.</summary>
    public Task SetViewAsync(float azimuthDeg, float elevationDeg)
    {
        return ExecuteScriptSafeAsync(FormattableString.Invariant(
            $"setCamera({azimuthDeg}, {elevationDeg})"));
    }

    /// <summary>◄ ► 90°-snap azimuth step (direction −1 or +1).</summary>
    public Task StepAzimuthAsync(int direction)
    {
        return ExecuteScriptSafeAsync($"stepAzimuth({Math.Sign(direction)})");
    }

    // ─── Control modes (orbit / fly / walk) ───────────────────────────────

    /// <summary>
    ///     Switch the camera control mode ("orbit", "fly", or "walk"). The page
    ///     echoes a controlMode message back, which keeps the toolbar in sync
    ///     (the same path F-key toggles inside the viewer arrive on).
    /// </summary>
    public Task SetControlModeAsync(string mode)
    {
        if (mode is not ("orbit" or "fly" or "walk")) return Task.CompletedTask;
        return ExecuteScriptSafeAsync($"setControlMode('{mode}')");
    }

    private async void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string mode }) return;
        if (mode == _controlMode)
        {
            // Re-clicking the active toggle would just uncheck it — restore.
            UpdateModeUi(mode);
            return;
        }

        // Reflect the click immediately; the page's echo confirms or corrects it.
        UpdateModeUi(mode);
        await SetControlModeAsync(mode);
    }

    /// <summary>Radio-group the three mode toggles and gate the projection combo
    /// (fly/walk are perspective-only; the combo applies to orbit).</summary>
    private void UpdateModeUi(string mode)
    {
        _controlMode = mode;
        OrbitModeButton.IsChecked = mode == "orbit";
        FlyModeButton.IsChecked = mode == "fly";
        WalkModeButton.IsChecked = mode == "walk";
        ProjectionCombo.IsEnabled = mode == "orbit";
    }

    private async void ProjectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectionCombo == null || ProjectionCombo.SelectedIndex < 0) return;
        await SetProjectionAsync((ViewerProjection)ProjectionCombo.SelectedIndex);
    }

    private async void ViewPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string name }) return;
        var (azimuth, elevation) = ViewerCameraPresets.NamedView(name);
        await SetViewAsync(azimuth, elevation);
    }

    private async void StepAzimuthLeft_Click(object sender, RoutedEventArgs e)
    {
        await StepAzimuthAsync(-1);
    }

    private async void StepAzimuthRight_Click(object sender, RoutedEventArgs e)
    {
        await StepAzimuthAsync(1);
    }

    // ─── Transport ────────────────────────────────────────────────────────

    private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            PlayPauseIcon.Glyph = GlyphPlay;
            await ExecuteScriptSafeAsync("pauseAnim()");
        }
        else
        {
            _isPlaying = true;
            PlayPauseIcon.Glyph = GlyphPause;
            await ExecuteScriptSafeAsync("playAnim()");
        }
    }

    private async void PlaybackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider) return;

        // Hold off incoming time ticks briefly so the thumb doesn't snap
        // forward out from under a scrub while the animation keeps playing.
        _suppressTimeUntil = DateTime.UtcNow.AddMilliseconds(500);
        CurrentTimeText.Text = e.NewValue.ToString("0.00");
        await ExecuteScriptSafeAsync(
            $"seekAnim({e.NewValue.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    private void HideTransport()
    {
        TransportPanel.Visibility = Visibility.Collapsed;
        _isPlaying = false;
        PlayPauseIcon.Glyph = GlyphPlay;
        _updatingSlider = true;
        PlaybackSlider.Value = 0;
        _updatingSlider = false;
        CurrentTimeText.Text = "0.00";
    }

    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? json;
        try
        {
            json = e.TryGetWebMessageAsString();
        }
        catch
        {
            return;
        }

        if (string.IsNullOrEmpty(json)) return;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString();

            switch (type)
            {
                case "loaded":
                    var animCount = root.TryGetProperty("animations", out var anims)
                        ? anims.GetArrayLength()
                        : 0;
                    _duration = root.TryGetProperty("duration", out var dur)
                        ? dur.GetDouble()
                        : 0;
                    HasAnimations = animCount > 0 && _duration > 0;
                    if (HasAnimations)
                    {
                        _updatingSlider = true;
                        PlaybackSlider.Maximum = _duration;
                        PlaybackSlider.Value = 0;
                        _updatingSlider = false;
                        TotalTimeText.Text = $"{_duration:0.00} s";
                        CurrentTimeText.Text = "0.00";
                        _isPlaying = true;
                        PlayPauseIcon.Glyph = GlyphPause;
                        TransportPanel.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        HideTransport();
                    }

                    // Keyboard reach: WASD/F land in the page only while the
                    // WebView has focus.
                    ModelWebView.Focus(FocusState.Programmatic);
                    ModelLoaded?.Invoke(this, EventArgs.Empty);
                    break;

                case "controlMode":
                    // Page-side mode changes (F key, level-load defaults,
                    // preset-triggered orbit returns) sync the toolbar here.
                    if (root.TryGetProperty("mode", out var modeProp)
                        && modeProp.GetString() is { } newMode)
                    {
                        UpdateModeUi(newMode);
                    }

                    break;

                case "time":
                    if (!root.TryGetProperty("value", out var time)) break;
                    if (DateTime.UtcNow < _suppressTimeUntil) break;
                    var t = time.GetDouble();
                    _updatingSlider = true;
                    PlaybackSlider.Value = Math.Min(t, PlaybackSlider.Maximum);
                    _updatingSlider = false;
                    CurrentTimeText.Text = t.ToString("0.00");
                    break;
            }
        }
        catch
        {
            // Malformed message from the page — ignore.
        }
    }

    private async Task ExecuteScriptSafeAsync(string script)
    {
        if (!_webViewInitialized) return;

        try
        {
            await ModelWebView.ExecuteScriptAsync(script);
        }
        catch
        {
            // WebView may not be ready yet or is shutting down.
        }
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
    }
}
