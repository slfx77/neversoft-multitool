namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     THPS2X frontend <c>Anm\0</c> timeline document. This is a UI format,
///     unrelated to the skeletal SKA animation family.
/// </summary>
internal sealed class Thps2XFrontendAnim
{
    public required uint Version { get; init; }
    public required float Duration { get; init; }
    public required Thps2XFrontendAnimNode[] Roots { get; init; }
    public required int SerializedSize { get; init; }
    public required int NodeCount { get; init; }
    public required int KeyCount { get; init; }
}

/// <summary>
///     One structurally nested frontend timeline node. The twelve base floats,
///     the following 32-bit value, and the key values remain deliberately
///     semantic-free: their serialized grammar is proven, but their complete
///     runtime meaning is not.
/// </summary>
internal sealed class Thps2XFrontendAnimNode
{
    public required int SerializedOffset { get; init; }
    public required int SerializedSize { get; init; }
    public required string Name { get; init; }
    public required float[] BaseValues { get; init; }
    public required uint RawUnknown32 { get; init; }
    public required Thps2XFrontendAnimKey[] Keys { get; init; }
    public required Thps2XFrontendAnimNode[] Children { get; init; }
    public required string ClosingName { get; init; }
}

/// <summary>
///     A 42-byte frontend timeline key: nine floats, one raw 16-bit value, and
///     one trailing float. The unusual unaligned final float is intentional.
/// </summary>
internal sealed class Thps2XFrontendAnimKey
{
    public required int SerializedOffset { get; init; }
    public required float[] Values { get; init; }
    public required ushort RawUnknown16 { get; init; }
    public required float TrailingValue { get; init; }
}
