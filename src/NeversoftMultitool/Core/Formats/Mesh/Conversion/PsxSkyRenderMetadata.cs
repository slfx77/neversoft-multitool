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
/// </summary>
public sealed record PsxSkyRenderMetadata(uint? SkyColor = null) : NativeRenderMetadata("psx_sky");
