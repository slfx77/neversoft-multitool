using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

/// <summary>
///     Keeps a three-panel column layout inside the window. The side panels
///     are pixel-sized (so on large windows every extra pixel goes to the star
///     middle column), which means a too-narrow window would push content off
///     the right edge. When the panels plus the middle column's minimum cannot
///     fit, this shrinks the left panel toward its MinWidth, then the right
///     panel toward its MinWidth. It only ever shrinks — regrowing stays the
///     user's call via the splitters.
/// </summary>
public sealed class PanelWidthClamp
{
    private readonly Grid _grid;
    private readonly ColumnDefinition _left;
    private readonly ColumnDefinition _middle;
    private readonly ColumnDefinition _right;
    private bool _updating;

    public PanelWidthClamp(
        Grid grid,
        ColumnDefinition left,
        ColumnDefinition middle,
        ColumnDefinition right)
    {
        _grid = grid;
        _left = left;
        _middle = middle;
        _right = right;
        // LayoutUpdated (not SizeChanged): once the columns overflow, the grid
        // is arranged at its inflated desired width, which no longer changes
        // when the window shrinks further — SizeChanged would stay silent.
        grid.LayoutUpdated += (_, _) => Apply();
    }

    private void Apply()
    {
        if (_updating) return;
        var root = _grid.XamlRoot;
        if (root == null || root.Size.Width <= 0) return;

        // The grid's own ActualWidth is useless as a bound: when the column
        // minimums exceed the layout slot, XAML arranges the grid (and every
        // inflated ancestor) at its DESIRED width and clips at the window, so
        // ActualWidth reports the overflowing size. Anchor on the window's
        // XamlRoot size instead; the only chrome right of this grid is its
        // parent's right padding.
        var origin = _grid.TransformToVisual(null)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var rightInset = (_grid.Parent as Grid)?.Padding.Right ?? 0;
        var available = root.Size.Width - origin.X - rightInset;
        if (available <= 0) return;

        var fixedGaps = _grid.ColumnDefinitions
            .Where(column => column != _left && column != _middle && column != _right)
            .Sum(column => column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth);

        var leftWidth = _left.Width.IsAbsolute ? _left.Width.Value : _left.ActualWidth;
        var rightWidth = _right.Width.IsAbsolute ? _right.Width.Value : _right.ActualWidth;
        var overflow = leftWidth + rightWidth + fixedGaps + _middle.MinWidth - available;
        if (overflow <= 0) return;

        _updating = true;
        try
        {
            var leftGive = Math.Min(overflow, Math.Max(0, leftWidth - _left.MinWidth));
            if (leftGive > 0) _left.Width = new GridLength(leftWidth - leftGive);

            overflow -= leftGive;
            if (overflow > 0)
            {
                var rightGive = Math.Min(overflow, Math.Max(0, rightWidth - _right.MinWidth));
                if (rightGive > 0) _right.Width = new GridLength(rightWidth - rightGive);
            }
        }
        finally
        {
            _updating = false;
        }
    }
}
