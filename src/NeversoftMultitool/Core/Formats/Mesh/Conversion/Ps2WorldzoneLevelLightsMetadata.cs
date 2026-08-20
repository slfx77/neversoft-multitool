using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Document-scope carrier for the zone's authored levellight nodes,
///     published as GLB scene extras (<c>neversoftLevelLights</c>). Positions
///     are stored in EXPORT space (authored world units × coordinateScale) so
///     they line up with the emitted geometry. Data-only in v1: nothing in
///     the converter applies these as lights — authored brightness is a
///     runtime placeholder and the TOD scripts own the live values, so
///     application policy belongs to consumers (viewer/Blender/tooling).
/// </summary>
public sealed record Ps2WorldzoneLevelLightsMetadata(
    IReadOnlyList<Ps2WorldzoneLevelLight> Lights)
    : NativeRenderMetadata("ps2_worldzone_level_lights");
