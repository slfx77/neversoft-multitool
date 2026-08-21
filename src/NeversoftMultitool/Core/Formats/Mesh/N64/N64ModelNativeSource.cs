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
/// <param name="TrickNamesForBank">
///     Given a bank's slot count, the trick names the cart's own
///     <c>tricks.bin</c> uniquely owns. Deferred rather than resolved eagerly
///     for two reasons: the slot count is not known until the animation plan
///     opens inside the writer, and a static export must not pay for a scan it
///     will not use. Empty for a cart with no table, and for slots several
///     tricks share.
/// </param>
public sealed record N64ModelNativeSource(
    byte[] ShellData,
    PsxMeshFile Shell,
    byte[]? RenderBank,
    uint? RenderBankId,
    Func<int, N64ModelCompanions.N64ResolvedTexture?> TextureProvider,
    N64LightRig? LightRig = null,
    Func<int, IReadOnlyDictionary<int, string>>? TrickNamesForBank = null)
    : ModelNativeSource(ModelSourceKind.N64Model);
