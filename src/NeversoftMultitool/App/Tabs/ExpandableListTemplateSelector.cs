using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NeversoftMultitool;

/// <summary>
///     Selects between parent and child DataTemplates based on IListEntry.IsChildEntry.
/// </summary>
public sealed class ExpandableListTemplateSelector : DataTemplateSelector
{
    public DataTemplate? ParentTemplate { get; set; }
    public DataTemplate? ChildTemplate { get; set; }

    protected override DataTemplate SelectTemplateCore(object item)
    {
        if (item is IListEntry entry && entry.IsChildEntry)
            return ChildTemplate!;
        return ParentTemplate!;
    }
}
