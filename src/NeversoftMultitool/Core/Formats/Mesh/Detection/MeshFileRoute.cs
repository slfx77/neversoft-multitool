namespace NeversoftMultitool.Core.Formats.Mesh.Detection;

/// <summary>
///     The classification of one mesh file: which parser owns it, which PS2
///     sub-format it is, and a display label. Produced by
///     <see cref="MeshTypeDetector" />.
/// </summary>
/// <param name="Kind">
///     The owning parser, or <see cref="MeshFileKind.None" /> when the file is
///     not a mesh or failed its content check.
/// </param>
/// <param name="Suffix">
///     The matched known suffix (e.g. <c>.skin.ps2</c>), or an empty string when
///     nothing matched.
/// </param>
/// <param name="Ps2SubFormat">
///     Load-bearing for <see cref="MeshFileKind.Ps2Scene" />: the conversion
///     pipeline hard-dispatches on it and throws when it is wrong, so a resolved
///     Ps2Scene route must never carry <see cref="Ps2SceneSubFormat.None" />.
/// </param>
/// <param name="RequiresContentProbe">
///     True when the name alone cannot decide — bare <c>.skin</c>/<c>.mdl</c>
///     before <see cref="MeshTypeDetector.DetectFromBytes" /> has run.
/// </param>
/// <param name="DisplayFormat">Human-readable format label, or null when unresolved.</param>
/// <param name="UnsupportedReason">
///     Why a file that carries a known mesh suffix is nonetheless not convertible
///     (bad version, wrong magic). Null when supported.
/// </param>
public readonly record struct MeshFileRoute(
    MeshFileKind Kind,
    string Suffix,
    Ps2SceneSubFormat Ps2SubFormat = Ps2SceneSubFormat.None,
    bool RequiresContentProbe = false,
    string? DisplayFormat = null,
    string? UnsupportedReason = null)
{
    /// <summary>The name carries a known mesh suffix, whatever the content says.</summary>
    public bool IsCandidate => Kind != MeshFileKind.None || RequiresContentProbe || UnsupportedReason != null;

    /// <summary>A parser owns this file and its content check passed.</summary>
    public bool IsSupported => Kind != MeshFileKind.None && UnsupportedReason == null;

    /// <summary>A route for a file that carries no known mesh suffix at all.</summary>
    public static MeshFileRoute NotAMesh(string extension)
    {
        return new MeshFileRoute(
            MeshFileKind.None,
            "",
            UnsupportedReason: $"Unrecognized mesh format: {extension}");
    }

    /// <summary>A route for a known suffix whose content check failed.</summary>
    public static MeshFileRoute Rejected(string suffix, string displayFormat, string reason)
    {
        return new MeshFileRoute(MeshFileKind.None, suffix, DisplayFormat: displayFormat, UnsupportedReason: reason);
    }
}
