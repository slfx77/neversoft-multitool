namespace NeversoftMultitool;

/// <summary>
/// Marker interface for items in an expandable list (parent files and child sub-items).
/// </summary>
public interface IListEntry
{
    bool IsChildEntry { get; }
}
