namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
///     A single command in a TRG command list.
/// </summary>
public sealed class TrgCommand
{
    public int Opcode { get; init; }
    public string Name { get; init; } = "";
    public List<object>? Args { get; init; }
}
