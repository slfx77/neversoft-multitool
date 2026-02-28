namespace NeversoftMultitool.Core.Formats.Trg;

public sealed class TrgPosition
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

public sealed class TrgAngles
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

/// <summary>
///     Light parameters from CLight constructor (Ghidra decompilation):
///     CLight(CVector&amp; pos, int nodeIndex, int range, int innerAngle, int outerAngle,
///     int falloff, Uc r1, Uc g1, Uc b1, Uc r2, Uc g2, Uc b2)
///     Node index passed externally; data contains 4 × int16 + 6 × uint8 = 14 bytes.
/// </summary>
public sealed class TrgLightParams
{
    public int Range { get; init; }
    public int InnerAngle { get; init; }
    public int OuterAngle { get; init; }
    public int Falloff { get; init; }
    public byte Color1R { get; init; }
    public byte Color1G { get; init; }
    public byte Color1B { get; init; }
    public byte Color2R { get; init; }
    public byte Color2G { get; init; }
    public byte Color2B { get; init; }
}
