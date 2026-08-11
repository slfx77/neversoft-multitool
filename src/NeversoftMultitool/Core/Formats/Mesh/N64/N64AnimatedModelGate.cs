using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Conservative eligibility gate for embedded N64 animation. It admits
///     direct-matrix <c>0x2A</c> and compressed <c>0x2C</c> supers whose render
///     geometry proves one unambiguous global-<c>G_MTX</c> binding plan. The
///     two interpretations are accepted when they coincide (the historical
///     object-zero/single-placement subset), or when every global joint is in
///     range and at least one placement-relative lookup is impossible. Cases
///     where both interpretations remain viable are deliberately rejected.
/// </summary>
internal static class N64AnimatedModelGate
{
    public static N64AnimatedModelPlan? TryOpen(
        byte[] shellData,
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes)
    {
        ArgumentNullException.ThrowIfNull(shellData);
        if (!TryCreateBindingPlan(shell, meshes, out var bindingPlan))
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

    internal static bool TryCreateBindingPlan(
        PsxMeshFile shell,
        IReadOnlyList<N64RenderBankFile.N64RenderMesh> meshes,
        out N64GeometryBindingPlan bindingPlan)
    {
        ArgumentNullException.ThrowIfNull(shell);
        ArgumentNullException.ThrowIfNull(meshes);
        bindingPlan = default;

        if (!shell.IsSuperModel || shell.Objects.Count == 0 || meshes.Count == 0)
            return false;

        var byNode = new Dictionary<int, N64RenderBankFile.N64RenderMesh>();
        foreach (var mesh in meshes)
        {
            if (!byNode.TryAdd(mesh.NodeIndex, mesh))
                return false;
        }

        var placements = new List<(int ObjectIndex, N64RenderBankFile.N64RenderMesh Mesh)>();
        var placedNodes = new HashSet<int>();
        for (var objectIndex = 0; objectIndex < shell.Objects.Count; objectIndex++)
        {
            if (!byNode.TryGetValue(shell.Objects[objectIndex].MeshIndex, out var mesh)
                || mesh.Triangles.Count == 0)
            {
                continue;
            }

            // A repeated node would become duplicate coincident geometry under
            // global matrices. Its runtime selection semantics are not proven.
            if (!placedNodes.Add(mesh.NodeIndex))
                return false;
            placements.Add((objectIndex, mesh));
        }

        if (placements.Count == 0)
            return false;

        var animated = N64GeometryBindingPlan.Animated(shell.Objects.Count);
        var rigid = N64GeometryBindingPlan.Static(shell.Objects.Count);
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

        // If both address modes are in range but select different joints, raw
        // geometry alone cannot decide between them (Spider-Man map/docock are
        // the corpus controls). Keep those shells static until a runtime or
        // Rosetta oracle settles them.
        if (!interpretationsCoincide && !relativeLookupFails)
            return false;

        bindingPlan = animated;
        return true;
    }
}

internal sealed record N64AnimatedModelPlan(
    N64CompressedAnimationBank Animations,
    N64GeometryBindingPlan Geometry);
