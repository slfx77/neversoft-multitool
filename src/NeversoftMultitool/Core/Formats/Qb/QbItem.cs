namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     A top-level item in a QB file: either a script definition or a global assignment.
/// </summary>
public sealed class QbItem
{
    public required QbItemKind Kind { get; init; }
    public uint NameChecksum { get; init; }
    public string? Name { get; init; }
    public int StartTokenIndex { get; init; }
    public int EndTokenIndex { get; init; }
}
