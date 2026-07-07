namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     One animation slot's entry in the per-PSX-file animation table:
///     where its frame data lives in the pool, how many frames it spans, and
///     the per-anim tween flag. When non-zero (v1 / 0x2A only), the payload
///     stores keyframes every <c>TweenFlag + 1</c> frames — both engine call
///     sites pass <c>framesPerKey = tweenFlag + 1</c> to
///     <c>M3dUtils_InterpolateVectors</c> (PERFECT-matched), which lerps the
///     intermediate frames with a truncating 1.12 factor. So even
///     <c>TweenFlag == 1</c> means half the frames are stored.
///     Layout per entry (8 bytes; same for v1 / 0x2A and v2 / 0x2C chunks):
///     <list type="bullet">
///         <item><c>+0x00</c>: <c>u32</c> data offset (relative to chunk start)</item>
///         <item><c>+0x04</c>: <c>u16</c> frame count (total playback frames)</item>
///         <item><c>+0x06</c>: <c>u16</c> tween flag (stored interval − 1)</item>
///     </list>
/// </summary>
public sealed record PsxAnimEntry(int PoolOffset, int FrameCount, int TweenFlag = 0);
