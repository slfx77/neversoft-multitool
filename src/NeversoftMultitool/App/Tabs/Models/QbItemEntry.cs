using NeversoftMultitool.Core.Formats.Qb;

namespace NeversoftMultitool;

public sealed class QbItemEntry : IListEntry
{
    public required string ParentFileName { get; init; }
    public required int ItemIndex { get; init; }
    public required QbItem Item { get; init; }
    public required QbFile QbFile { get; init; }

    public string TypeDisplay => Item.Kind == QbItemKind.Script ? "SCRIPT" : "GLOBAL";
    public string IndexDisplay => $"#{ItemIndex:D3}";
    public string SummaryDisplay => Item.Name ?? $"#\"0x{Item.NameChecksum:X8}\"";

    public bool IsChildEntry => true;
}
