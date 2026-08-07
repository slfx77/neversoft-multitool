using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Builds the document's colour-pulse channel table and resolves which
///     channel (if any) feeds a given face corner.
///     <para>
///         A pulse targets a palette index, but the exported colour depends on
///         the FACE as well: whether it is textured, whether it is
///         semi-transparent, and at which ABR blend rate. Additive and
///         subtractive faces move the animation out of RGB and into alpha
///         entirely (<see cref="PsxGeometryHelpers.ApplyPsxUntexturedBlend" />).
///         So a channel is keyed by (pulse x transform class), not by pulse
///         alone — a key that omitted the blend rate would apply a fire's alpha
///         ramp as an opaque surface's RGB ramp, which is silent, wrong, and
///         still looks like a plausible animation.
///     </para>
/// </summary>
public sealed class PsxColourPulseChannels
{
    /// <summary>
    ///     The bake lerps in the 0..255 byte domain then divides; a consumer
    ///     lerps already-normalized keys. Same maths, different float rounding,
    ///     so allow ~1e-3. A genuinely mis-bound channel is out by 0.1-0.7.
    /// </summary>
    private const float FrameZeroTolerance = 1e-3f;

    private readonly List<ModelColourPulseChannel> _channels = [];
    private readonly Dictionary<ChannelKey, int> _index = [];
    private readonly Dictionary<PsxMeshFile, Dictionary<byte, PsxColourPulse>> _pulsesByFile = [];

    /// <summary>The document-scoped table, in channel-index order (index 0 is channel 1).</summary>
    public IReadOnlyList<ModelColourPulseChannel> Channels => _channels;

    public bool HasChannels => _channels.Count > 0;

    /// <summary>
    ///     The 1-based channel for one face corner, or 0 when that corner's
    ///     colour is static.
    /// </summary>
    /// <param name="exportedColor">
    ///     The colour the writer is about to store on this corner. The channel is
    ///     only handed out when its own frame 0 reproduces this value — see the
    ///     remarks on <see cref="ReproducesExportedColor" />.
    /// </param>
    public int Resolve(
        PsxMeshFile? file,
        PsxFace face,
        byte paletteIndex,
        bool usesDisplayRgb,
        bool emitPacket,
        bool ps1TexturedModulation,
        Vector4 exportedColor)
    {
        if (file == null || file.ColourPulses.Count == 0 || !face.IsGouraud)
            return 0;

        var pulses = GetPulseMap(file);
        if (!pulses.TryGetValue(paletteIndex, out var pulse) || pulse.Keys.Length == 0)
            return 0;

        var key = new ChannelKey(
            file,
            paletteIndex,
            usesDisplayRgb,
            emitPacket,
            ps1TexturedModulation,
            face.IsSemiTransparent && !(face.IsTextured && face.TextureHash != 0),
            face.BlendRate);

        if (_index.TryGetValue(key, out var existing))
        {
            return ReproducesExportedColor(_channels[existing - 1], exportedColor) ? existing : 0;
        }

        var channel = BuildChannel(pulse, key);
        if (!ReproducesExportedColor(channel, exportedColor))
            return 0;

        _channels.Add(channel);
        var oneBased = _channels.Count;
        _index[key] = oneBased;
        return oneBased;
    }

    /// <summary>
    ///     The animation is only correct if it passes through the colour that was
    ///     actually exported: the vertex holds the static bake, and the consumer
    ///     starts from frame 0. Checking that here rather than trusting the
    ///     transform chain makes the invariant hold BY CONSTRUCTION — any corner
    ///     whose colour this class cannot reproduce simply does not animate and
    ///     keeps its existing static appearance, instead of pulsing to a colour
    ///     the engine never showed.
    /// </summary>
    private static bool ReproducesExportedColor(ModelColourPulseChannel channel, Vector4 exportedColor)
    {
        var frameZero = EvaluateFrameZero(channel);
        return Math.Abs(frameZero.X - exportedColor.X) <= FrameZeroTolerance
               && Math.Abs(frameZero.Y - exportedColor.Y) <= FrameZeroTolerance
               && Math.Abs(frameZero.Z - exportedColor.Z) <= FrameZeroTolerance
               && Math.Abs(frameZero.W - exportedColor.W) <= FrameZeroTolerance;
    }

    private static Vector4 EvaluateFrameZero(ModelColourPulseChannel channel)
    {
        var keyIndex = channel.InitialKeyIndex < channel.PacketKeys.Count ? channel.InitialKeyIndex : 0;
        var time = (int)channel.InitialAccumulator;

        for (var guard = 0; guard < 256; guard++)
        {
            var interval = channel.Intervals[keyIndex];
            if (interval == 0 || time < interval)
                break;
            time -= interval;
            keyIndex = (byte)((keyIndex + 1) % channel.PacketKeys.Count);
        }

        var current = channel.PacketKeys[keyIndex];
        var next = channel.PacketKeys[(keyIndex + 1) % channel.PacketKeys.Count];
        var amount = channel.Intervals[keyIndex] == 0
            ? 0f
            : Math.Clamp(time / (float)channel.Intervals[keyIndex], 0f, 1f);
        return Vector4.Lerp(current, next, amount);
    }

    private Dictionary<byte, PsxColourPulse> GetPulseMap(PsxMeshFile file)
    {
        if (_pulsesByFile.TryGetValue(file, out var existing))
            return existing;

        var map = new Dictionary<byte, PsxColourPulse>();
        foreach (var pulse in file.ColourPulses)
            map[pulse.ColourIndex] = pulse;

        _pulsesByFile[file] = map;
        return map;
    }

    /// <summary>
    ///     Runs every key of the pulse through the SAME transform chain the
    ///     static bake uses, so channel key 0 evaluated at frame 0 reproduces the
    ///     baked vertex colour exactly.
    /// </summary>
    private static ModelColourPulseChannel BuildChannel(PsxColourPulse pulse, ChannelKey key)
    {
        var packet = new Vector4[pulse.Keys.Length];
        var portable = new Vector4[pulse.Keys.Length];
        var intervals = new byte[pulse.Keys.Length];

        for (var i = 0; i < pulse.Keys.Length; i++)
        {
            var k = pulse.Keys[i];
            intervals[i] = k.Interval;

            // 1. Palette entry, in the same 0..1 domain PsxLibrary stores.
            var palette = new Vector4(k.R / 255f, k.G / 255f, k.B / 255f, 1f);

            // 2. ResolvePaletteColor: textured PS1 primitives modulate around
            //    128, untextured ones use ordinary display RGB.
            var resolved = key.UsesDisplayRgb
                ? palette
                : new Vector4(
                    palette.X * (255f / 128f),
                    palette.Y * (255f / 128f),
                    palette.Z * (255f / 128f),
                    palette.W);

            // 3. The untextured semi-transparent collapse, which is what moves
            //    additive/subtractive animation into alpha.
            var blended = key.IsUntexturedSemiTransparent
                ? ApplyBlend(key.BlendRate, resolved)
                : resolved;

            portable[i] = PsxGeometryHelpers.DisplayRgbToLinear(blended, key.Ps1TexturedModulation);

            // _PSX_COLOR_0 is written as "PsxPacketColor ?? Color", so a file
            // that emits no PS1 packet (v6) stores the LINEAR colour there, not
            // the display-domain one. Matching that is what makes frame 0 equal
            // the bake on DC/PC files as well as PS1 ones.
            packet[i] = key.EmitPacket
                ? PsxGeometryHelpers.ToPsxPacketColor(blended, key.Ps1TexturedModulation)
                : portable[i];
        }

        return new ModelColourPulseChannel(
            packet,
            portable,
            intervals,
            pulse.InitialKeyIndex,
            pulse.InitialTimeAccumulator);
    }

    /// <summary>
    ///     Mirrors <see cref="PsxGeometryHelpers.ApplyPsxUntexturedBlend" /> for a
    ///     colour whose face context is already reduced to a blend rate.
    /// </summary>
    private static Vector4 ApplyBlend(int blendRate, Vector4 color)
    {
        var luminance = Math.Max(color.X, Math.Max(color.Y, color.Z));
        return blendRate switch
        {
            1 => new Vector4(1f, 1f, 1f, color.W * luminance),
            2 => new Vector4(0f, 0f, 0f, color.W * luminance),
            3 => new Vector4(1f, 1f, 1f, color.W * luminance * 0.25f),
            _ => new Vector4(color.X, color.Y, color.Z, color.W * 0.5f)
        };
    }

    /// <summary>
    ///     The dedup key. Every field changes the exported colour, so all of them
    ///     have to participate — see the class remarks.
    /// </summary>
    private readonly record struct ChannelKey(
        PsxMeshFile File,
        byte PaletteIndex,
        bool UsesDisplayRgb,
        bool EmitPacket,
        bool Ps1TexturedModulation,
        bool IsUntexturedSemiTransparent,
        int BlendRate);
}
