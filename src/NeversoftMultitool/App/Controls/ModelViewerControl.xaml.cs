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

    /// <summary>Load a GLB into the viewer. Animated models start playing.</summary>
    public async Task LoadGlbAsync(byte[] glbBytes)
    {
        LastGlbBytes = glbBytes;
        HasAnimations = false;
        HideTransport();

        if (!_webViewInitialized) return;

        // Base64 of a multi-MB GLB is CPU-bound — keep it off the UI thread.
        var base64 = await Task.Run(() => Convert.ToBase64String(glbBytes));
        await ExecuteScriptSafeAsync($"loadModel('{base64}')");
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

                    ModelLoaded?.Invoke(this, EventArgs.Empty);
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
