using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Native payload for a model bundle carved from an N64 ROM: the PSX
///     shell (object table, hierarchy, mesh name hashes, animation chunk),
///     the raw render-bank record its <c>renderbank-id.bin</c> points at, and
///     the texture provider keyed by PS1 texture id.
///     <para>
///         <see cref="RenderBank" /> is kept as raw bytes because its vertex
///         codec is not yet decoded; consumers that only need the skeleton or
///         names ignore it.
///     </para>
/// </summary>
public sealed record N64ModelNativeSource(
    PsxMeshFile Shell,
    byte[]? RenderBank,
    uint? RenderBankId,
    MeshChecksumTextureResolver TextureProvider)
    : ModelNativeSource(ModelSourceKind.N64Model);
