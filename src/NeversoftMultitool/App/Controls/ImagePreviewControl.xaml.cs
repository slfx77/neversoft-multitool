using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace NeversoftMultitool;

/// <summary>
///     Shared zoomable image preview: "Zoom to fit" scales the image to the
///     available pane; "100%" shows it 1:1 with drag-to-pan when it overflows.
///     With Filtering off, fit mode instead snaps to the largest integer scale
///     that fits and upscales nearest-neighbor for crisp pixels.
/// </summary>
public sealed partial class ImagePreviewControl : UserControl
{
    private const int CheckerboardMaximumDimension = 1024;
    private const double CheckerboardCellSize = 16;

    private WriteableBitmap? _bitmap;
    private int _checkerboardCellSize;
    private int _checkerboardHeight;
    private bool _checkerboardPending;
    private int _checkerboardWidth;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartH;
    private double _dragStartV;
    private bool _rescalePending;
    private WriteableBitmap? _scaledBitmap;
    private int _scaledScale;
    private WriteableBitmap? _scaledSourceOf;

    public ImagePreviewControl()
    {
        InitializeComponent();
    }

    private bool IsActualSize => ZoomModeCombo.SelectedIndex == 1;

    private bool IsFilteringEnabled => FilteringCheckbox?.IsChecked != false;

    private static WriteableBitmap CreateCheckerboard(int width, int height, int cellSize)
    {
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var light = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                var value = light ? (byte)148 : (byte)96;
                var offset = (y * width + x) * 4;
                pixels[offset] = value;
                pixels[offset + 1] = value;
                pixels[offset + 2] = value;
                pixels[offset + 3] = byte.MaxValue;
            }
        }

        var bitmap = new WriteableBitmap(width, height);
        using (var stream = bitmap.PixelBuffer.AsStream())
            stream.Write(pixels, 0, pixels.Length);
        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Show an image (clears loading + placeholder states).</summary>
    public void SetSource(WriteableBitmap bitmap)
    {
        _bitmap = bitmap;
        TransparencyBackground.Visibility = Visibility.Visible;
        QueueCheckerboardUpdate();
        DimensionsText.Text = $"{bitmap.PixelWidth} × {bitmap.PixelHeight}";
        PlaceholderText.Visibility = Visibility.Collapsed;
        SetLoading(false);
        ApplyZoomMode();
    }

    /// <summary>Clear the image and show the placeholder.</summary>
    public void Clear()
    {
        _bitmap = null;
        _scaledBitmap = null;
        _scaledSourceOf = null;
        PreviewImage.Source = null;
        TransparencyBackground.Visibility = Visibility.Collapsed;
        DimensionsText.Text = "";
        PlaceholderText.Visibility = Visibility.Visible;
        SetLoading(false);
    }

    public void SetLoading(bool loading)
    {
        LoadingRing.IsActive = loading;
        LoadingRing.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
        if (loading)
            PlaceholderText.Visibility = Visibility.Collapsed;
    }

    private void ZoomModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ApplyZoomMode();
    }

    private void FilteringCheckbox_Changed(object sender, RoutedEventArgs e)
    {
        ApplyZoomMode();
    }

    private void Scroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Integer-fit scale depends on the viewport; recompute after resizes,
        // coalescing bursts of layout passes into one low-priority pass.
        if (_bitmap == null || IsActualSize || IsFilteringEnabled || _rescalePending)
            return;

        _rescalePending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _rescalePending = false;
            ApplyZoomMode();
        });
    }

    private void TransparencyBackground_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (TransparencyBackground.Visibility == Visibility.Visible)
            QueueCheckerboardUpdate();
    }

    private void QueueCheckerboardUpdate()
    {
        if (_checkerboardPending) return;

        _checkerboardPending = true;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _checkerboardPending = false;
                UpdateCheckerboard();
            }))
        {
            _checkerboardPending = false;
        }
    }

    private void UpdateCheckerboard()
    {
        var displayWidth = TransparencyBackground.ActualWidth;
        var displayHeight = TransparencyBackground.ActualHeight;
        if (!double.IsFinite(displayWidth) || !double.IsFinite(displayHeight) ||
            displayWidth < 1 || displayHeight < 1)
        {
            return;
        }

        // Match the source bitmap's aspect ratio to the pane. Stretch=Fill can
        // then scale both axes by the same amount, preserving square checks.
        // Cap the bitmap so a very large/maximized pane does not allocate a
        // full display-sized backing buffer merely for a repeating pattern.
        var divisor = Math.Max(
            1,
            Math.Max(displayWidth, displayHeight) / CheckerboardMaximumDimension);
        var width = Math.Max(1, (int)Math.Round(displayWidth / divisor));
        var height = Math.Max(1, (int)Math.Round(displayHeight / divisor));
        var cellSize = Math.Max(1, (int)Math.Round(CheckerboardCellSize / divisor));
        if (width == _checkerboardWidth
            && height == _checkerboardHeight
            && cellSize == _checkerboardCellSize)
        {
            return;
        }

        TransparencyBackground.Source = CreateCheckerboard(width, height, cellSize);
        _checkerboardWidth = width;
        _checkerboardHeight = height;
        _checkerboardCellSize = cellSize;
    }

    private void ApplyZoomMode()
    {
        // ZoomModeCombo's SelectedIndex="0" raises SelectionChanged during
        // InitializeComponent, before these later-declared elements exist.
        if (Scroller is null || PreviewImage is null) return;

        if (_bitmap == null)
        {
            PreviewImage.Source = null;
            return;
        }

        if (IsActualSize)
        {
            // 1:1 pixels — natural size, scrollbars when larger than the pane.
            PreviewImage.Source = _bitmap;
            PreviewImage.Stretch = Stretch.None;
            SetScrollEnabled(true);
        }
        else if (IsFilteringEnabled)
        {
            // Fit — ScrollViewer constrains the image to the viewport and
            // Uniform stretch scales it to fill the shorter axis.
            PreviewImage.Source = _bitmap;
            PreviewImage.Stretch = Stretch.Uniform;
            SetScrollEnabled(false);
            Scroller.ChangeView(0, 0, null, true);
        }
        else
        {
            // Pixel-perfect fit — largest integer scale that fits the viewport,
            // upscaled nearest-neighbor on the CPU (WinUI's Image offers no
            // interpolation-mode control). Scale 1 may still overflow tiny
            // viewports, so scrolling stays available.
            var scale = ComputeIntegerFitScale();
            PreviewImage.Source = GetScaledBitmap(scale);
            PreviewImage.Stretch = Stretch.None;
            SetScrollEnabled(true);
        }
    }

    private void SetScrollEnabled(bool enabled)
    {
        Scroller.HorizontalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Scroller.VerticalScrollBarVisibility = enabled ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        Scroller.HorizontalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;
        Scroller.VerticalScrollMode = enabled ? ScrollMode.Enabled : ScrollMode.Disabled;
    }

    private int ComputeIntegerFitScale()
    {
        if (_bitmap == null) return 1;

        var viewportW = Scroller.ActualWidth;
        var viewportH = Scroller.ActualHeight;
        if (viewportW < 1 || viewportH < 1) return 1;

        var scale = (int)Math.Min(viewportW / _bitmap.PixelWidth, viewportH / _bitmap.PixelHeight);
        return Math.Max(1, scale);
    }

    private WriteableBitmap GetScaledBitmap(int scale)
    {
        if (_bitmap == null) throw new InvalidOperationException();
        if (scale <= 1) return _bitmap;
        if (_scaledBitmap != null && ReferenceEquals(_scaledSourceOf, _bitmap) && _scaledScale == scale)
            return _scaledBitmap;

        var srcW = _bitmap.PixelWidth;
        var srcH = _bitmap.PixelHeight;
        var src = _bitmap.PixelBuffer.ToArray();
        var dstW = srcW * scale;
        var dstH = srcH * scale;
        var dst = new byte[dstW * dstH * 4];
        var dstRowBytes = dstW * 4;

        for (var y = 0; y < srcH; y++)
        {
            // Build one horizontally-replicated row, then stamp it `scale` times.
            var rowStart = y * scale * dstRowBytes;
            for (var x = 0; x < srcW; x++)
            {
                var s = (y * srcW + x) * 4;
                for (var r = 0; r < scale; r++)
                {
                    var d = rowStart + (x * scale + r) * 4;
                    dst[d] = src[s];
                    dst[d + 1] = src[s + 1];
                    dst[d + 2] = src[s + 2];
                    dst[d + 3] = src[s + 3];
                }
            }

            for (var r = 1; r < scale; r++)
                Buffer.BlockCopy(dst, rowStart, dst, rowStart + r * dstRowBytes, dstRowBytes);
        }

        var scaled = new WriteableBitmap(dstW, dstH);
        using (var stream = scaled.PixelBuffer.AsStream())
        {
            stream.Write(dst, 0, dst.Length);
        }

        scaled.Invalidate();
        _scaledBitmap = scaled;
        _scaledSourceOf = _bitmap;
        _scaledScale = scale;
        return scaled;
    }

    // ─── Drag-to-pan (100% / pixel-perfect modes) ─────────────────────────

    private void PreviewImage_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_bitmap == null || (!IsActualSize && IsFilteringEnabled)) return;

        _dragging = true;
        _dragStart = e.GetCurrentPoint(Scroller).Position;
        _dragStartH = Scroller.HorizontalOffset;
        _dragStartV = Scroller.VerticalOffset;
        PreviewImage.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void PreviewImage_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;

        var pos = e.GetCurrentPoint(Scroller).Position;
        Scroller.ChangeView(
            _dragStartH - (pos.X - _dragStart.X),
            _dragStartV - (pos.Y - _dragStart.Y),
            null,
            true);
        e.Handled = true;
    }

    private void PreviewImage_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PreviewImage.ReleasePointerCaptures();
    }
}
