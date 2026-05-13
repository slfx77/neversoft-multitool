using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using NeversoftMultitool.Core.Formats.Animation;

namespace NeversoftMultitool;

/// <summary>
///     Owns the WebView2 model viewer for the Character Preview tab. Builds
///     single-animation GLBs on demand and loads them into the embedded
///     <c>&lt;model-viewer&gt;</c> via base64. Mirrors the cancellation pattern
///     used by <see cref="MeshConverterTabPreview" />.
/// </summary>
internal sealed class CharacterPreviewTabPreview : IDisposable
{
    private readonly DispatcherQueue _dispatcher;
    private readonly TextBlock _errorText;
    private readonly TextBlock _infoText;
    private readonly ProgressRing _loadingRing;
    private readonly WebView2 _webView;

    private byte[]? _lastGlbBytes;
    private CancellationTokenSource? _previewCts;

    private bool _webViewInitialized;

    public CharacterPreviewTabPreview(
        WebView2 webView,
        ProgressRing loadingRing,
        TextBlock infoText,
        TextBlock errorText,
        DispatcherQueue dispatcher)
    {
        _webView = webView;
        _loadingRing = loadingRing;
        _infoText = infoText;
        _errorText = errorText;
        _dispatcher = dispatcher;
    }

    /// <summary>
    ///     The most recently built single-animation preview GLB, if any. Used
    ///     by "Render GIF…" so it doesn't have to rebuild the bytes.
    /// </summary>
    public byte[]? LastGlbBytes => _lastGlbBytes;

    public void Dispose()
    {
        _previewCts?.Dispose();
        _previewCts = null;
    }

    public async Task InitializeAsync()
    {
        if (_webViewInitialized) return;

        try
        {
            await _webView.EnsureCoreWebView2Async();

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "character-viewer-assets",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            _webView.CoreWebView2.Navigate(
                new UriBuilder(Uri.UriSchemeHttps, "character-viewer-assets")
                    { Path = "mesh-viewer.html" }.Uri.ToString());
            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            _errorText.Text = $"WebView2 init failed: {ex.Message}";
            _errorText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    ///     Build a single-animation preview GLB and push it into the viewer.
    ///     Cancels any in-flight preview build first.
    /// </summary>
    public async Task LoadPreviewAsync(MeshFileEntry character, AnimationProbe animation)
    {
        var previousCts = _previewCts;
        if (previousCts != null)
        {
            _previewCts = null;
            await previousCts.CancelAsync();
            previousCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        var token = cts.Token;

        _errorText.Visibility = Visibility.Collapsed;
        _infoText.Text = $"Building preview for {animation.DisplayName}…";
        _loadingRing.IsActive = true;
        _loadingRing.Visibility = Visibility.Visible;
        _lastGlbBytes = null;

        if (_webViewInitialized)
        {
            try
            {
                await _webView.ExecuteScriptAsync("setStatus('Building preview...')");
            }
            catch
            {
                /* WebView may not be ready */
            }
        }

        try
        {
            var result = await Task.Run(
                () => CharacterAnimationConverter.BuildAnimatedGlb(character, [animation]),
                token);

            if (token.IsCancellationRequested) return;

            if (result.GlbBytes == null || result.Triangles == 0)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _infoText.Text = "";
                    _errorText.Text = result.Error ?? "Preview build returned no geometry.";
                    _errorText.Visibility = Visibility.Visible;
                    _loadingRing.IsActive = false;
                    _loadingRing.Visibility = Visibility.Collapsed;
                });
                if (_webViewInitialized)
                {
                    try
                    {
                        await _webView.ExecuteScriptAsync("setStatus('No preview')");
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                return;
            }

            _lastGlbBytes = result.GlbBytes;
            var base64 = Convert.ToBase64String(result.GlbBytes);

            _dispatcher.TryEnqueue(() =>
            {
                _infoText.Text =
                    $"{character.FormatDisplay} | {animation.DisplayName} | "
                    + $"{animation.DurationSec:0.00} s | {result.Triangles:N0} triangles";
                _loadingRing.IsActive = false;
                _loadingRing.Visibility = Visibility.Collapsed;
            });

            if (_webViewInitialized)
                await _webView.ExecuteScriptAsync($"loadModel('{base64}')");
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly.
        }
        catch (Exception ex)
        {
            if (token.IsCancellationRequested) return;

            _dispatcher.TryEnqueue(() =>
            {
                _infoText.Text = "";
                _errorText.Text = $"Preview failed: {ex.Message}";
                _errorText.Visibility = Visibility.Visible;
                _loadingRing.IsActive = false;
                _loadingRing.Visibility = Visibility.Collapsed;
            });
        }
    }

    public async Task ClearAsync()
    {
        var cts = _previewCts;
        if (cts != null)
        {
            _previewCts = null;
            await cts.CancelAsync();
            cts.Dispose();
        }

        _lastGlbBytes = null;
        _infoText.Text = "";
        _errorText.Visibility = Visibility.Collapsed;
        _loadingRing.IsActive = false;
        _loadingRing.Visibility = Visibility.Collapsed;

        if (_webViewInitialized)
        {
            try
            {
                await _webView.ExecuteScriptAsync("clearModel()");
            }
            catch
            {
                /* ignore */
            }
        }
    }
}
