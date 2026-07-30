using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace NeversoftMultitool;

/// <summary>
///     Collapses a side-panel grid column down to a slim strip and restores it
///     on demand, preserving whatever width the user last dragged the splitter
///     to. The panel content and the strip swap visibility; the adjacent
///     splitter hides while collapsed so its gap column stays as card spacing.
/// </summary>
public sealed class PanelCollapseController
{
    private const double StripWidth = 36;

    private readonly ColumnDefinition _column;
    private readonly UIElement _content;
    private readonly UIElement _splitter;
    private readonly UIElement _strip;
    private bool _collapsed;
    private double _expandedMinWidth;
    private GridLength _expandedWidth;

    public PanelCollapseController(
        ColumnDefinition column,
        UIElement splitter,
        UIElement content,
        UIElement strip,
        ButtonBase collapseButton,
        ButtonBase expandButton)
    {
        _column = column;
        _splitter = splitter;
        _content = content;
        _strip = strip;
        collapseButton.Click += (_, _) => SetCollapsed(true);
        expandButton.Click += (_, _) => SetCollapsed(false);
    }

    public void SetCollapsed(bool collapsed)
    {
        if (_collapsed == collapsed) return;
        _collapsed = collapsed;

        if (collapsed)
        {
            _expandedWidth = _column.Width;
            _expandedMinWidth = _column.MinWidth;
            // MinWidth stays at the strip width (not 0) so PanelWidthClamp
            // can never squeeze the collapsed strip out of existence.
            _column.MinWidth = StripWidth;
            _column.Width = new GridLength(StripWidth);
            _splitter.Visibility = Visibility.Collapsed;
            _content.Visibility = Visibility.Collapsed;
            _strip.Visibility = Visibility.Visible;
        }
        else
        {
            _column.MinWidth = _expandedMinWidth;
            _column.Width = _expandedWidth;
            _splitter.Visibility = Visibility.Visible;
            _strip.Visibility = Visibility.Collapsed;
            _content.Visibility = Visibility.Visible;
        }
    }
}
