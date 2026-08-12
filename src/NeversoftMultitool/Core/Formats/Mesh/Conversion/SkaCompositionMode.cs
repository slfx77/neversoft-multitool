namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal enum SkaCompositionMode
{
    Raw,
    BindComposed,

    // THPS3's RenderWare interpolator exposes raw XYZW Q/T tracks, but the
    // final RwMatrix palette uses the conjugate of that runtime quaternion and
    // the translation verbatim. This policy is deliberately separate from the
    // Neversoft SKE paths, whose raw channels already use export-space values.
    Thps3Runtime
}
