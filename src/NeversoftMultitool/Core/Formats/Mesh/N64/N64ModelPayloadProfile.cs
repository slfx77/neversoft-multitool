using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Fail-closed exceptions to the structural N64 model rules. A profile is
///     selected only by the complete shell/render payload identity and its
///     render-bank link; carved slot names are deliberately not consulted.
/// </summary>
internal sealed record N64ModelPayloadProfile(
    string Identity,
    N64GeometryBindingMode AnimatedBindingMode,
    float VertexScaleFactor)
{
    internal const int SpiderMapShellLength = 1_776;
    internal const int SpiderMapRenderBankLength = 41_552;
    internal const uint SpiderMapRenderBankId = 215;
    internal const string SpiderMapShellSha256 =
        "2712A50ED97F86E34B603FC8C6E736C2D8985FBC834DBAA5AFECA2BA9D0F2BD9";
    internal const string SpiderMapRenderBankSha256 =
        "F1439FD75A9448DF5DE15EC352749AFF9E55E50B5030707C7BB5D45776CEE65A";

    private static readonly byte[] SpiderMapShellDigest =
        Convert.FromHexString(SpiderMapShellSha256);
    private static readonly byte[] SpiderMapRenderBankDigest =
        Convert.FromHexString(SpiderMapRenderBankSha256);

    private static readonly N64ModelPayloadProfile SpiderMap = new(
        "spider-map-relative-k1",
        N64GeometryBindingMode.AnimatedRelative,
        1f);

    public static N64ModelPayloadProfile? TryResolve(
        byte[] shellData,
        byte[]? renderBankData,
        uint? renderBankId)
    {
        ArgumentNullException.ThrowIfNull(shellData);
        if (renderBankData == null
            || renderBankId != SpiderMapRenderBankId
            || shellData.Length != SpiderMapShellLength
            || renderBankData.Length != SpiderMapRenderBankLength)
        {
            return null;
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(shellData, digest);
        if (!digest.SequenceEqual(SpiderMapShellDigest))
            return null;

        SHA256.HashData(renderBankData, digest);
        return digest.SequenceEqual(SpiderMapRenderBankDigest) ? SpiderMap : null;
    }

    /// <summary>
    ///     Existing corpus rule outside exact exceptions: super render
    ///     vertices carry the measured ×8 correction, other shells ×1.
    /// </summary>
    public static float DefaultVertexScaleFactor(PsxMeshFile shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        return shell.IsSuperModel ? 8f : 1f;
    }
}
