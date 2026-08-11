namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Custom event attached to a Neversoft SKA animation. The event header is
///     common to every custom-key type; type-specific values are decoded when
///     their payload grammar is known, while <see cref="Payload" /> preserves
///     the complete payload for inspection and forward compatibility.
/// </summary>
public sealed class SkaCustomKey
{
    /// <summary>
    ///     Raw authored event timestamp. THUG interprets this field at 60 Hz,
    ///     but the corresponding THAW v0x28 runtime unit has not been proven.
    /// </summary>
    public required uint Timestamp { get; init; }

    /// <summary>Raw engine event-type identifier.</summary>
    public required uint Type { get; init; }

    /// <summary>Total serialized record size, including the 12-byte header.</summary>
    public required uint Size { get; init; }

    /// <summary>Raw serialized payload bytes in the source file's byte order.</summary>
    public required byte[] Payload { get; init; }

    /// <summary>Type 1 camera horizontal field of view, in radians.</summary>
    public float? Fov { get; init; }

    /// <summary>Type 4 script QbKey.</summary>
    public uint? ScriptQbKey { get; init; }

    /// <summary>Stable descriptive name for <see cref="Type" />.</summary>
    public string Name => GetTypeName(Type);

    internal static string GetTypeName(uint type)
    {
        return type switch
        {
            0 => "unused",
            1 => "changeFocalLength",
            2 => "changeCameraRt",
            3 => "changeCameraRtIgnore",
            4 => "runScript",
            5 => "createObjectFromStruct",
            6 => "killObjectFromStruct",
            7 => "changeCameraRtEnd",
            _ => "unknown"
        };
    }
}
