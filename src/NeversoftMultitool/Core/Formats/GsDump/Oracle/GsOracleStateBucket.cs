namespace NeversoftMultitool.Core.Formats.GsDump.Oracle;

/// <summary>
///     One GS state bucket the game actually drew with while sampling a
///     texture: the blend/test register facts a converter's material
///     classification can be adjudicated against, plus frame-global draw
///     ordering. Field semantics mirror <see cref="GsMaterialAuditRow" />.
/// </summary>
internal sealed class GsOracleStateBucket
{
    public required string Primitive { get; init; }
    public bool AlphaBlendEnabled { get; init; }
    public uint AlphaA { get; init; }
    public uint AlphaB { get; init; }
    public uint AlphaC { get; init; }
    public uint AlphaD { get; init; }
    public uint AlphaFix { get; init; }
    public bool AlphaTestEnabled { get; init; }
    public uint AlphaTestMethod { get; init; }
    public uint AlphaRef { get; init; }
    public uint AlphaFailMode { get; init; }
    public uint TexaTa0 { get; init; }
    public bool TexaAem { get; init; }
    public uint TexaTa1 { get; init; }
    public uint TextureTfx { get; init; }
    public uint TextureTcc { get; init; }
    public uint FramebufferMask { get; init; }
    public uint FramebufferPsm { get; init; }
    public bool DepthTestEnabled { get; init; }
    public uint DepthTestMethod { get; init; }
    public bool ZMask { get; init; }
    public bool FramebufferAlphaWriteEnabled { get; init; }
    public long Draws { get; init; }
    public long PixelsWritten { get; init; }
    public long FirstDrawIndex { get; init; }
    public long LastDrawIndex { get; init; }
}
