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

/// <summary>
///     Document-scope record of the TRG's <c>SetSkyColor</c> backdrop — the
///     colour the engine clears the framebuffer to every frame
///     (<c>Db_UpdateSky</c>: <c>Draw.isbg = 1</c> with this RGB; the default
///     after <c>Db_Init</c> is black, and 0xFFFF/0xFFFF disables clearing).
/// </summary>
/// <remarks>
///     Separate from <see cref="PsxSkyRenderMetadata" />, which rides sky-dome
///     MESHES: a region can name a backdrop colour while owning no dome at all
///     (skny's two-player bank SkNY_O2 has no background object, but its
///     RESTART nodes still issue SetSkyColor (0,9,25) — the night-blue clear
///     is the ONLY sky that region has). The glTF exporter publishes this as
///     scene extras <c>neversoftSkyBackdrop</c>; the viewer uses it as the
///     background when the model brings no sky meshes of its own. Packing
///     matches the mesh-level value: <c>R&lt;&lt;16 | G&lt;&lt;8 | B</c>.
///     Note the fog is NOT this colour: the engine's depth-cue fades toward
///     <c>M3d_FadeColour</c> (TRG 0xC8), an independent register — skny sets
///     backdrop (0,9,25) but fade (25,9,0).
/// </summary>
public sealed record PsxSkyBackdropMetadata(uint SkyColor)
    : NativeRenderMetadata("psx_sky_backdrop");
