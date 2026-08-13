using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Evaluates a PS1 colour pulse (tagged chunk 7) at a point in time.
///     <para>
///         The engine advances each pulse's playhead by the frame delta
///         (<c>XblanksNow - XblanksThen</c>), wraps the key index modulo the key
///         count, and GTE-interpolates current -> next RGB by
///         <c>TimeAccumulator / Interval</c>. Intervals are therefore in 60 Hz
///         frames — the same clock the UV wibble already runs on.
///     </para>
///     <para>
///         Frame 0 reproduces the serialized pre-tick playhead, which is exactly
///         what the static palette bake writes. Anything that animates a pulse
///         must agree with the bake at frame 0.
///     </para>
/// </summary>
public static class PsxColourPulseEvaluator
{
    /// <summary>
    ///     Backstop for a malformed key list. The walk is bounded by the key
    ///     count once <see cref="WrapIntoCycle" /> has folded the playhead into
    ///     one cycle, so this only ever trips on data the cycle length cannot
    ///     describe (every interval zero).
    /// </summary>
    private const int WalkGuard = 256;

    /// <summary>
    ///     RGB in the ordinary 0..255 domain at <paramref name="frameOffset" />
    ///     frames past the pulse's serialized playhead.
    /// </summary>
    public static Vector3 Evaluate(
        PsxColourPulseKey[] keys,
        byte initialKeyIndex,
        byte initialTimeAccumulator,
        int frameOffset = 0)
    {
        if (keys.Length == 0)
            return Vector3.Zero;

        var keyIndex = initialKeyIndex < keys.Length ? initialKeyIndex : 0;
        var time = initialTimeAccumulator + frameOffset;

        // Fold the playhead into one cycle BEFORE walking. Walking a raw frame
        // count one interval at a time cannot reach a far-future playhead: the
        // guard would stop the walk early, leaving time >= interval so the blend
        // clamped to 1 and the pulse froze on that key for the rest of playback.
        if (!WrapIntoCycle(keys, ref time))
            return KeyColor(keys[keyIndex]);

        for (var guard = 0; guard < WalkGuard; guard++)
        {
            var interval = keys[keyIndex].Interval;
            if (interval == 0 || time < interval)
                break;
            time -= interval;
            keyIndex = (keyIndex + 1) % keys.Length;
        }

        var current = keys[keyIndex];
        var next = keys[(keyIndex + 1) % keys.Length];
        var amount = current.Interval == 0
            ? 0f
            : Math.Clamp(time / (float)current.Interval, 0f, 1f);

        return Vector3.Lerp(KeyColor(current), KeyColor(next), amount);
    }

    /// <summary>
    ///     Folds <paramref name="time" /> into <c>[0, cycle)</c> so the key walk
    ///     is bounded by the key count rather than by the elapsed frame count.
    ///     Returns false when the pulse has no cycle at all (every interval
    ///     zero), i.e. it holds its current key forever.
    /// </summary>
    private static bool WrapIntoCycle(PsxColourPulseKey[] keys, ref int time)
    {
        var cycle = CycleFrames(keys);
        if (cycle <= 0)
            return false;

        time = ((time % cycle) + cycle) % cycle;
        return true;
    }

    /// <summary>
    ///     Frames for one full cycle through the key list. Zero when every
    ///     interval is zero, i.e. the pulse holds a single colour forever.
    /// </summary>
    public static int CycleFrames(PsxColourPulseKey[] keys)
    {
        var total = 0;
        foreach (var key in keys)
            total += key.Interval;
        return total;
    }

    private static Vector3 KeyColor(PsxColourPulseKey key)
    {
        return new Vector3(key.R, key.G, key.B);
    }
}
