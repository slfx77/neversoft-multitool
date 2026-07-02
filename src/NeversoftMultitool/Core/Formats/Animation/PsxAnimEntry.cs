namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One animation slot's entry in the per-PSX-file animation table:
///     where its frame data lives in the pool, how many frames it spans, and
///     the per-anim tween flag the engine consults to decide whether to invoke
///     <c>M3dUtils_InBetween</c> (non-zero ⇒ frame interpolation active).
///     Layout per entry (8 bytes; same for v1 / 0x2A and v2 / 0x2C chunks):
///     <list type="bullet">
///         <item><c>+0x00</c>: <c>u32</c> data offset (relative to chunk start)</item>
///         <item><c>+0x04</c>: <c>u16</c> frame count</item>
///         <item><c>+0x06</c>: <c>u16</c> tween flag</item>
///     </list>
/// </summary>
public sealed record PsxAnimEntry(int PoolOffset, int FrameCount, int TweenFlag = 0);
