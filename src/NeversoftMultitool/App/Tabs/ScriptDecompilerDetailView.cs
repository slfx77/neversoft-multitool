using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

internal sealed class ScriptDecompilerDetailView(
    Border detailPanel,
    ColumnDefinition detailSplitterColumn,
    GridSplitter detailSplitter,
    ColumnDefinition detailColumn,
    TextBlock detailTypeText,
    TextBlock detailIndexText,
    StackPanel propertiesSection,
    Grid propertiesGrid,
    StackPanel positionSection,
    TextBlock detailPositionText,
    StackPanel anglesSection,
    TextBlock detailAnglesText,
    StackPanel linksSection,
    TextBlock detailLinksText,
    StackPanel commandsSection,
    TextBlock commandsHeaderText,
    ItemsRepeater commandsRepeater,
    StackPanel scriptSection,
    TextBlock scriptHeaderText,
    ItemsRepeater scriptRepeater,
    StackPanel rawHexSection,
    TextBlock detailRawHexText,
    StackPanel sourceSection,
    TextBlock sourceHeaderText,
    TextBlock detailSourceText)
{
    public Border DetailPanel { get; } = detailPanel;
    public ColumnDefinition DetailSplitterColumn { get; } = detailSplitterColumn;
    public GridSplitter DetailSplitter { get; } = detailSplitter;
    public ColumnDefinition DetailColumn { get; } = detailColumn;
    public TextBlock DetailTypeText { get; } = detailTypeText;
    public TextBlock DetailIndexText { get; } = detailIndexText;
    public StackPanel PropertiesSection { get; } = propertiesSection;
    public Grid PropertiesGrid { get; } = propertiesGrid;
    public StackPanel PositionSection { get; } = positionSection;
    public TextBlock DetailPositionText { get; } = detailPositionText;
    public StackPanel AnglesSection { get; } = anglesSection;
    public TextBlock DetailAnglesText { get; } = detailAnglesText;
    public StackPanel LinksSection { get; } = linksSection;
    public TextBlock DetailLinksText { get; } = detailLinksText;
    public StackPanel CommandsSection { get; } = commandsSection;
    public TextBlock CommandsHeaderText { get; } = commandsHeaderText;
    public ItemsRepeater CommandsRepeater { get; } = commandsRepeater;
    public StackPanel ScriptSection { get; } = scriptSection;
    public TextBlock ScriptHeaderText { get; } = scriptHeaderText;
    public ItemsRepeater ScriptRepeater { get; } = scriptRepeater;
    public StackPanel RawHexSection { get; } = rawHexSection;
    public TextBlock DetailRawHexText { get; } = detailRawHexText;
    public StackPanel SourceSection { get; } = sourceSection;
    public TextBlock SourceHeaderText { get; } = sourceHeaderText;
    public TextBlock DetailSourceText { get; } = detailSourceText;
}
