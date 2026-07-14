using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace NeversoftMultitool;

/// <summary>
///     Rounds the corners of elements that render through their own swap
///     chain (WebView2, MediaPlayerElement) and therefore ignore the rounded
///     corners of ancestor Borders. Applies a composition RectangleClip with
///     corner radii to the element's visual and keeps it sized on layout.
/// </summary>
public static class RoundedClipHelper
{
    public static void Apply(FrameworkElement element, float radius)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var clip = visual.Compositor.CreateRectangleClip();
        var corner = new Vector2(radius, radius);
        clip.TopLeftRadius = corner;
        clip.TopRightRadius = corner;
        clip.BottomLeftRadius = corner;
        clip.BottomRightRadius = corner;
        visual.Clip = clip;

        Resize(element, clip);
        element.SizeChanged += (_, _) => Resize(element, clip);
    }

    private static void Resize(FrameworkElement element, RectangleClip clip)
    {
        clip.Right = (float)element.ActualWidth;
        clip.Bottom = (float)element.ActualHeight;
    }
}
