using NeversoftMultitool.Core.Formats.Texture.Nds;

namespace NeversoftMultitool.Core.Formats.Mesh.Nds;

/// <summary>
///     Finds the texture bank a DS model draws from, without knowing either file's
///     name.
///
///     A model's bank is <c>.\&lt;idA&gt;.textureinfo.bin</c> and its geometry
///     <c>.\&lt;idA&gt;.&lt;idB&gt;.geometry.bin</c> — the same idA — but neither id
///     is stored anywhere in the container (measured: no bare u32 hashes to a
///     textureinfo name above chance), and recovering them by CRC preimage is the
///     search already refuted, because the 8-hex space IS the CRC-32 codomain.
///
///     A different join needs no names. Both sides independently declare the same
///     GX state: a bank record stores a full <c>texImageParam</c>, and the model's
///     TEXIMAGE_PARAM site carries the same size and format bits with only the VRAM
///     address blanked. So a bank is COMPATIBLE with a model when, for every site,
///     the texture index is in range and the size/format bits agree.
///
///     The true bank always satisfies that, so it is always among the candidates.
///     Where exactly one bank survives, it therefore IS the model's bank — a proof,
///     not a guess. Where several survive, they disagree about the actual texel
///     blob (measured: candidate sets never agree on a pixel id when there is more
///     than one), so nothing is bound rather than something plausible.
///
///     Coverage: 463/866 Sk8land, 280/946 Downhill Jam, 324/1330 Proving Ground
///     textured models resolve uniquely.
///
///     The comparison deliberately excludes bit 29. Banks set the colour-0 transparency
///     flag on 99-197 records per cart while no model site ever does, so including it
///     rejects the true bank for about a sixth of all models.
/// </summary>
public static class NdsTextureBankResolver
{
    /// <summary>Size (bits 20-25) and format (26-28); the bits both sides agree on.</summary>
    private static uint FormatKey(uint texImageParam)
    {
        return (texImageParam >> 20) & 0x1FF;
    }

    /// <summary>
    ///     Returns the one bank compatible with every textured group, or null when
    ///     none or several are.
    /// </summary>
    public static IReadOnlyList<NdsTextureEntry>? Resolve(
        IReadOnlyList<NdsGeometryGroup> groups,
        IReadOnlyList<IReadOnlyList<NdsTextureEntry>> banks)
    {
        var constraints = groups
            .Where(group => group.Indices.Count > 0
                            && group.Material.HasTexture
                            && group.Material.TextureIndex >= 0)
            .Select(group => (group.Material.TextureIndex, group.Material.TexImageParam))
            .Distinct()
            .ToArray();
        if (constraints.Length == 0)
            return null;

        IReadOnlyList<NdsTextureEntry>? found = null;
        foreach (var bank in banks)
        {
            if (!IsCompatible(bank, constraints))
                continue;
            if (found != null)
                return null; // ambiguous, and candidates disagree on the texture
            found = bank;
        }

        return found;
    }

    private static bool IsCompatible(
        IReadOnlyList<NdsTextureEntry> bank, (int Index, uint Param)[] constraints)
    {
        foreach (var (index, param) in constraints)
        {
            if (index >= bank.Count)
                return false;
            if (FormatKey(bank[index].TexImageParam) != FormatKey(param))
                return false;
        }

        return true;
    }
}
