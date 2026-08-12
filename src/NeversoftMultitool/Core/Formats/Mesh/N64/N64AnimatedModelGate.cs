using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Conservative eligibility gate for embedded N64 animation. It normally
///     admits direct-matrix <c>0x2A</c> and compressed <c>0x2C</c> supers only
///     when their geometry proves one unambiguous global-<c>G_MTX</c> binding
///     plan. One flat Spider-Man map instead has a cross-platform oracle for
///     placement-relative joints and ×1 vertices; that exception requires an
///     exact shell/render/id payload profile. Other flat supers where both
///     interpretations remain viable are deliberately rejected.
/// </summary>
internal static class N64AnimatedModelGate
{
    public static N64AnimatedModelPlan? TryOpen(
        byte[] shellData,
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        return TryOpen(shellData, shell, renderBankData: null, renderBankId: null, meshes);
    }

    public static N64AnimatedModelPlan? TryOpen(
        byte[] shellData,
        PsxMeshFile shell,
        byte[]? renderBankData,
        uint? renderBankId,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(shellData);
        var profile = N64ModelPayloadProfile.TryResolve(
            shellData, renderBankData, renderBankId);
        if (!TryCreateBindingPlan(shell, meshes, profile, out var bindingPlan))
            return null;

        var bank = N64CompressedAnimationBank.TryParse(shellData);
        return bank == null ? null : new N64AnimatedModelPlan(bank, bindingPlan);
    }

    /// <summary>
    ///     Geometry half of the conservative gate, kept separate from the
    ///     embedded-bank check so the corpus can pin both boundaries.
    /// </summary>
    internal static bool IsGeometryEligible(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        return TryCreateBindingPlan(shell, meshes, out _);
    }

    internal static bool IsGeometryEligible(
        byte[] shellData,
        PsxMeshFile shell,
        byte[]? renderBankData,
        uint? renderBankId,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(shellData);
        var profile = N64ModelPayloadProfile.TryResolve(
            shellData, renderBankData, renderBankId);
        return TryCreateBindingPlan(shell, meshes, profile, out _);
    }

    /// <summary>
    ///     Selects the rigid plan before animation opt-in. An exact payload's
    ///     scale exception is used only if the same unique-placement and
    ///     all-corner structural checks required for its animated plan pass;
    ///     any mismatch retains the historical shell-derived scale.
    /// </summary>
    internal static N64GeometryBindingPlan CreateStaticBindingPlan(
        byte[] shellData,
        PsxMeshFile shell,
        byte[]? renderBankData,
        uint? renderBankId,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(shellData);
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(meshes);

        var fallback = N64GeometryBindingPlan.StaticRelative(
            shell.Objects.Count,
            N64ModelPayloadProfile.DefaultVertexScaleFactor(shell));
        var profile = N64ModelPayloadProfile.TryResolve(
            shellData, renderBankData, renderBankId);
        if (profile == null
            || !TryCollectPlacements(shell, meshes, out var placements)
            || !TryCreateProfileBindingPlan(
                shell, meshes, placements, profile, out _))
        {
            return fallback;
        }

        return N64GeometryBindingPlan.StaticRelative(
            shell.Objects.Count, profile.VertexScaleFactor);
    }

    internal static bool TryCreateBindingPlan(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes,
        out N64GeometryBindingPlan bindingPlan)
    {
        return TryCreateBindingPlan(shell, meshes, profile: null, out bindingPlan);
    }

    private static bool TryCreateBindingPlan(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes,
        N64ModelPayloadProfile? profile,
        out N64GeometryBindingPlan bindingPlan)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(meshes);
        bindingPlan = default;

        if (!TryCollectPlacements(shell, meshes, out var placements))
            return false;

        if (profile != null)
            return TryCreateProfileBindingPlan(shell, meshes, placements, profile, out bindingPlan);

        var vertexScaleFactor = N64ModelPayloadProfile.DefaultVertexScaleFactor(shell);
        var animated = N64GeometryBindingPlan.AnimatedGlobal(
            shell.Objects.Count, vertexScaleFactor);
        var rigid = N64GeometryBindingPlan.StaticRelative(
            shell.Objects.Count, vertexScaleFactor);
        var interpretationsCoincide = true;
        var relativeLookupFails = false;

        bool ValidateCorner(int objectIndex, N64RenderBankFile.N64Corner corner)
        {
            if (!animated.TryResolveOffsetObjectIndex(
                    objectIndex, corner.MatrixIndex, out var globalIndex))
            {
                return false;
            }

            if (!rigid.TryResolveOffsetObjectIndex(
                    objectIndex, corner.MatrixIndex, out var relativeIndex))
            {
                relativeLookupFails = true;
                interpretationsCoincide = false;
            }
            else if (relativeIndex != globalIndex)
            {
                interpretationsCoincide = false;
            }

            return true;
        }

        foreach (var (objectIndex, mesh) in placements)
        {
            foreach (var triangle in mesh.Triangles)
            {
                if (!ValidateCorner(objectIndex, triangle.C0)
                    || !ValidateCorner(objectIndex, triangle.C1)
                    || !ValidateCorner(objectIndex, triangle.C2))
                {
                    return false;
                }
            }
        }

        // HIER supers use positional part order: RenderSuperItem draws
        // ppModels[part] and the animation track at that same part index.
        // Spider-Man docock supplies the cross-platform oracle for the one
        // otherwise-ambiguous corpus shape: its N64 shell is field-identical
        // to PSX, every G_MTX m vertex belongs to PSX positional mesh m, and
        // all 43 compressed clips decode sample-for-sample identically.
        if (!interpretationsCoincide
            && !relativeLookupFails
            && !PsxMeshSemantics.UsesCharacterObjectOrder(shell))
        {
            return false;
        }

        bindingPlan = animated;
        return true;
    }

    private static bool TryCollectPlacements(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes,
        out List<(int ObjectIndex, N64RenderBankFile.N64RenderMesh Mesh)> placements)
    {
        placements = [];
        if (!shell.IsSuperModel || shell.Objects.Count == 0 || meshes.Count == 0)
            return false;

        var byNode = new Dictionary<int, N64RenderBankFile.N64RenderMesh>();
        foreach (var mesh in meshes)
        {
            if (!byNode.TryAdd(mesh.NodeIndex, mesh))
                return false;
        }

        var placedNodes = new HashSet<int>();
        for (var objectIndex = 0; objectIndex < shell.Objects.Count; objectIndex++)
        {
            if (!byNode.TryGetValue(shell.Objects[objectIndex].MeshIndex, out var mesh)
                || mesh.Triangles.Count == 0)
            {
                continue;
            }

            // A repeated node has no unique placement ownership. Its runtime
            // selection semantics are not proven for either animated mode.
            if (!placedNodes.Add(mesh.NodeIndex))
                return false;
            placements.Add((objectIndex, mesh));
        }

        if (placements.Count == 0)
            return false;

        return true;
    }

    private static bool TryCreateProfileBindingPlan(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes,
        IReadOnlyList<(int ObjectIndex, N64RenderBankFile.N64RenderMesh Mesh)> placements,
        N64ModelPayloadProfile profile,
        out N64GeometryBindingPlan bindingPlan)
    {
        bindingPlan = default;
        if (profile.AnimatedBindingMode is not N64GeometryBindingMode.AnimatedRelative
            || shell.HasHierarchy
            || shell.Objects.Count != 12
            || meshes.Count != 12
            || placements.Count != shell.Objects.Count)
        {
            return false;
        }

        var relative = N64GeometryBindingPlan.AnimatedRelative(
            shell.Objects.Count, profile.VertexScaleFactor);
        foreach (var (objectIndex, mesh) in placements)
        {
            foreach (var triangle in mesh.Triangles)
            {
                if (!relative.TryResolveOffsetObjectIndex(
                        objectIndex, triangle.C0.MatrixIndex, out _)
                    || !relative.TryResolveOffsetObjectIndex(
                        objectIndex, triangle.C1.MatrixIndex, out _)
                    || !relative.TryResolveOffsetObjectIndex(
                        objectIndex, triangle.C2.MatrixIndex, out _))
                {
                    return false;
                }
            }
        }

        bindingPlan = relative;
        return true;
    }
}

internal sealed record N64AnimatedModelPlan(
    N64CompressedAnimationBank Animations,
    N64GeometryBindingPlan Geometry);
