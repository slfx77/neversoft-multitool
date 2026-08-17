using System.Text.Json;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     The viewer page's <c>loaded</c> message, exactly as the page sends it.
/// </summary>
/// <remarks>
///     <para>
///         Mirrors <c>postLoaded()</c> in <c>Assets/mesh-viewer.html</c> — the sole emitter
///         of <c>type: 'loaded'</c>, so a clip switch re-posting the message carries the same
///         surface-animation presence flags as the original load. The field names on both
///         sides are held together by <c>ViewerLoadedMessageContractTests</c>, the same
///         arrangement as <see cref="CapturedView" />.
///     </para>
///     <para>
///         Parsing lives in Core rather than the WinUI control because <c>App/**</c> is
///         excluded from the cross-platform target and the test project builds
///         <c>net10.0</c> only.
///     </para>
/// </remarks>
public readonly record struct ViewerLoadedMessage(
    int AnimationCount,
    double Duration,
    bool HasColourPulses,
    bool HasTextureWibbles)
{
    /// <summary>Whether the model has playable skeletal animation.</summary>
    public bool HasAnimations => AnimationCount > 0 && Duration > 0;

    /// <summary>
    ///     Read the page's <c>loaded</c> payload. Every field is optional and
    ///     defaults to zero/false, so a stale cached page that predates the
    ///     presence flags parses as "no surface animations" instead of failing.
    /// </summary>
    public static ViewerLoadedMessage Parse(JsonElement root)
    {
        var animationCount = root.TryGetProperty("animations", out var animations)
                             && animations.ValueKind == JsonValueKind.Array
            ? animations.GetArrayLength()
            : 0;
        var duration = root.TryGetProperty("duration", out var durationValue)
                       && durationValue.ValueKind == JsonValueKind.Number
                       && durationValue.TryGetDouble(out var parsedDuration)
                       && double.IsFinite(parsedDuration)
            ? parsedDuration
            : 0;
        return new ViewerLoadedMessage(
            animationCount,
            duration,
            ReadBool(root, "hasColourPulses"),
            ReadBool(root, "hasTextureWibbles"));
    }

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
}
