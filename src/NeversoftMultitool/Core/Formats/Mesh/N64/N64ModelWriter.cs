using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Populates a <see cref="ModelDocument" /> from a carved N64 model
///     bundle. The bundle splits the same way the Xbox DDM path does — a
///     placement/skeleton container (<c>geometry.psx.n64</c>) plus a separate
///     geometry container (<c>group2/</c>) — so the skeleton, bone names and
///     hierarchy come from the shell exactly as they do for a PS1 character.
///     <para>
///         Render geometry is NOT emitted yet: the group2 vertex codec is
///         undecoded, so a converted bundle currently carries its rig and
///         naming only. That is a deliberate, visible state rather than a
///         silent empty export — <see cref="N64ModelRenderMetadata" /> records
///         the bank id and byte size so callers can report it.
///     </para>
/// </summary>
public static class N64ModelWriter
{
    public static void Populate(ModelDocument document, N64ModelNativeSource source)
    {
        var shell = source.Shell;

        // Object table + HIER parents alone; no mesh data is consulted.
        document.Skeletons.Add(PsxSkinnedGeometryWriter.BuildPsxSkeleton(
            shell, pshFile: null, flatSkeleton: false, flatBoneIndices: null));

        document.NativeMetadata.Add(new N64ModelRenderMetadata(
            source.RenderBankId,
            source.RenderBank?.Length ?? 0,
            shell.Objects.Count,
            GeometryDecoded: false));
    }
}

/// <summary>
///     Carried into the export so a caller can tell an N64 bundle's rig-only
///     state from a genuinely empty model.
/// </summary>
public sealed record N64ModelRenderMetadata(
    uint? RenderBankId,
    int RenderBankBytes,
    int ObjectCount,
    bool GeometryDecoded) : NativeRenderMetadata("n64Model");
