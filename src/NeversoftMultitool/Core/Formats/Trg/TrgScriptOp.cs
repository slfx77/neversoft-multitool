namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
///     A single operation in a TRG bytecode script.
/// </summary>
public sealed class TrgScriptOp
{
    public string Opcode { get; init; } = "";
    public string Name { get; init; } = "";
    public object? Value { get; set; }
}
