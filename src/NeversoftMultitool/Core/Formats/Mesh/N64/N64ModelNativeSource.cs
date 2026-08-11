using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Native payload for a model bundle carved from an N64 ROM: the PSX
///     shell (object table, hierarchy, mesh name hashes, animation chunk),
///     the raw shell bytes (needed for its mixed-endian embedded animation),
///     the render-bank record its <c>renderbank-id.bin</c> points at, and the
///     texture provider keyed by dictionary slot.
///     <para>
///         <see cref="RenderBank" /> stays raw at this boundary and is decoded
///         by <see cref="N64RenderBankFile" /> while the document is populated;
///         consumers that only need the shell can ignore it.
///     </para>
/// </summary>
public sealed record N64ModelNativeSource(
    byte[] ShellData,
    PsxMeshFile Shell,
    byte[]? RenderBank,
    uint? RenderBankId,
    Func<int, N64ModelCompanions.N64ResolvedTexture?> TextureProvider,
    N64LightRig? LightRig = null)
    : ModelNativeSource(ModelSourceKind.N64Model);
