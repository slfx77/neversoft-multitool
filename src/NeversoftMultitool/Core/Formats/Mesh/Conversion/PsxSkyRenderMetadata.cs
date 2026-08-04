namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Marks a primitive as belonging to a PSX sky/background layer
///     (<see cref="PsxSkyDomeClassifier" /> — TRG BackgroundCreate join). The
///     glTF exporter publishes it as mesh extras <c>neversoftSky</c> (+
///     <c>neversoftSkyColor</c>, the TRG SetSkyColor backdrop RGB); the in-app
///     viewer renders tagged meshes first without depth writes, camera-locks
///     them per frame (the engine zeroes the GTE translation for backgrounds),
///     draws them unlit, and excludes them from framing and the walk-mode
///     ground raycast; the Blender importer files tagged objects into a
///     hideable NeversoftSky collection.
///
///     <paramref name="LayerIndex" /> is the layer's paint rank within the sky
///     pass: 0 draws first (furthest back), higher ranks paint over it. It comes
///     from the TRG BackgroundCreate registration order — see
///     <see cref="PsxSkyDomeClassifier.Result" /> for why that order is the
///     engine's paint order. Without it a multi-layer sky is ordered by
///     three.js object id, which put l2a1's dome over its skyline.
/// </summary>
public sealed record PsxSkyRenderMetadata(uint? SkyColor = null, int LayerIndex = 0)
    : NativeRenderMetadata("psx_sky");
