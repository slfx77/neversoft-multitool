using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     One EMITTED worldzone leaf's render state, as the converter decided it —
///     the shared GS-state readout behind the triage CSV. Raw ALPHA/TEST/FRAME
///     registers ride along so the dump can decode fields the classifier does
///     not currently read.
/// </summary>
public readonly record struct Ps2GeomMaterialDebugRecord(
    string MdlName,
    int LeafIndex,
    string Space,
    int DrawIndex,
    int PassIndex,
    int OverlapGroup,
    bool IsBillboard,
    ulong Alpha1,
    ulong Test1,
    ulong Frame1,
    string AlphaMode,
    string RenderLayer,
    uint RenderOrderKey,
    uint GroupChecksum,
    ulong Tex0,
    uint TextureChecksum,
    bool UsedSyntheticDestinationAlpha,
    bool TextureResolved,
    string ResolveMode,
    string SourceLabel,
    string EntryLabel,
    string BakeClass,
    int VertexCount,
    Vector3 Min,
    Vector3 Max);
