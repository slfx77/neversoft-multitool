namespace NeversoftMultitool.Core.Formats.Qb;

/// <summary>
///     A single parsed token from a QB file.
/// </summary>
public sealed class QbToken
{
    public required QbTokenType Type { get; init; }
    public long Offset { get; init; }

    // Payload fields — populated based on Type
    public uint NameChecksum { get; set; }
    public int IntValue { get; set; }
    public uint HexValue { get; set; }
    public float FloatValue { get; set; }
    public string? StringValue { get; set; }
    public float FloatX { get; set; }
    public float FloatY { get; set; }
    public float FloatZ { get; set; }
    public int JumpOffset { get; set; }
    public int RandomItemCount { get; set; }
    public (ushort Weight, int JumpOffset)[]? RandomItems { get; set; }
}
