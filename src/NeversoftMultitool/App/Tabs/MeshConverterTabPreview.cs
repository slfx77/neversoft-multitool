using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace NeversoftMultitool;

/// <summary>
///     Handles 3D model preview for MeshConverterTab using WebView2 + model-viewer.
///     Converts selected mesh files to GLB on a background thread, then loads
///     the result into an interactive 3D viewer via base64.
/// </summary>
internal sealed class MeshConverterTabPreview : IDisposable
{
    private readonly WebView2 _webView;
    private readonly ProgressRing _loadingRing;
    private readonly TextBlock _infoText;
    private readonly TextBlock _errorText;
    private readonly DispatcherQueue _dispatcher;

    private bool _webViewInitialized;
    private CancellationTokenSource? _previewCts;

    public MeshConverterTabPreview(
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

    public async Task InitializeAsync()
    {
        if (_webViewInitialized) return;

        try
        {
            await _webView.EnsureCoreWebView2Async();

            var assetsDir = Path.Combine(AppContext.BaseDirectory, "Assets");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "mesh-viewer-assets",
                assetsDir,
                CoreWebView2HostResourceAccessKind.Allow);

            _webView.CoreWebView2.Navigate(
                new UriBuilder(Uri.UriSchemeHttps, "mesh-viewer-assets") { Path = "mesh-viewer.html" }.Uri.ToString());
            _webViewInitialized = true;
        }
        catch (Exception ex)
        {
            _errorText.Text = $"WebView2 init failed: {ex.Message}";
            _errorText.Visibility = Visibility.Visible;
        }
    }

    public async Task LoadPreviewAsync(MeshFileEntry entry)
    {
        // Cancel any in-flight preview
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
        _infoText.Text = $"Converting {entry.FileName}...";
        _loadingRing.IsActive = true;
        _loadingRing.Visibility = Visibility.Visible;

        if (_webViewInitialized)
        {
            try { await _webView.ExecuteScriptAsync("setStatus('Converting...')"); }
            catch { /* WebView may not be ready */ }
        }

        try
        {
            var (glbBytes, triangles) = await Task.Run(() =>
                MeshConverterTabFileConverter.ConvertToGlbBytes(entry), token);

            if (token.IsCancellationRequested) return;

            if (glbBytes == null || glbBytes.Length == 0)
            {
                _dispatcher.TryEnqueue(() =>
                {
                    _infoText.Text = "No geometry in this file";
                    _loadingRing.IsActive = false;
                    _loadingRing.Visibility = Visibility.Collapsed;
                });
                if (_webViewInitialized)
                    await _webView.ExecuteScriptAsync("setStatus('No geometry')");
                return;
            }

            var base64 = Convert.ToBase64String(glbBytes);

            _dispatcher.TryEnqueue(() =>
            {
                _infoText.Text = $"{entry.FormatDisplay} | {triangles:N0} triangles | {glbBytes.Length / 1024:N0} KB";
                _loadingRing.IsActive = false;
                _loadingRing.Visibility = Visibility.Collapsed;
            });

            if (_webViewInitialized)
                await _webView.ExecuteScriptAsync($"loadModel('{base64}')");
        }
        catch (OperationCanceledException)
        {
            // Expected when switching selection rapidly
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

            if (_webViewInitialized)
            {
                try
                {
                    await _webView.ExecuteScriptAsync(
                        $"setStatus('Error: {EscapeJsString(ex.Message)}')");
                }
                catch { /* WebView may not be ready */ }
            }
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

        _infoText.Text = "";
        _errorText.Visibility = Visibility.Collapsed;
        _loadingRing.IsActive = false;
        _loadingRing.Visibility = Visibility.Collapsed;

        if (_webViewInitialized)
        {
            try { await _webView.ExecuteScriptAsync("clearModel()"); }
            catch { /* ignore */ }
        }
    }

    private static string EscapeJsString(string s)
    {
        return s.Replace("\\", "\\\\").Replace("'", "\\'").Replace("\n", "\\n").Replace("\r", "");
    }

    public void Dispose()
    {
        _previewCts?.Dispose();
        _previewCts = null;
    }
}
