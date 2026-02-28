using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool;

public sealed class TrgNodeEntry : IListEntry
{
    public required string ParentFileName { get; init; }
    public required int NodeIndex { get; init; }
    public required TrgNode Node { get; init; }

    public string TypeDisplay => Node.Type;
    public string IndexDisplay => $"#{NodeIndex:D3}";

    public string SummaryDisplay
    {
        get
        {
            var parts = new List<string>();
            if (Node.Name != null) parts.Add(Node.Name);
            if (Node.SubTypeName != null) parts.Add(Node.SubTypeName);
            else if (Node.PickupTypeName != null) parts.Add(Node.PickupTypeName);
            else if (Node.CameraModeName != null) parts.Add(Node.CameraModeName);
            if (Node.LightParams is { } lp)
                parts.Add($"RGB({lp.Color1R},{lp.Color1G},{lp.Color1B})");
            if (Node.Position != null)
                parts.Add($"({Node.Position.X:F1}, {Node.Position.Y:F1}, {Node.Position.Z:F1})");
            return parts.Count > 0 ? string.Join(" | ", parts) : "";
        }
    }

    public bool IsChildEntry => true;
}
