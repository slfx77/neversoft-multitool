using System.Numerics;
using SharpGLTF.Schema2;

namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     glTF morph-target evaluation for the software renderer.
/// </summary>
/// <remarks>
///     Sources with no skeleton animate entirely through morph weights — the GBA
///     skater blends complete posed vertex sets — so a loader that evaluates only
///     node TRS renders their bind pose at every frame instead of the clip.
/// </remarks>
internal static class GlbMorphTargets
{
    /// <summary>
    ///     The morph weights in effect for <paramref name="node" /> at
    ///     <paramref name="time" />, or null when nothing morphs. Null keeps the
    ///     caller on the untouched accessor arrays, so a morph-free file renders
    ///     exactly as it did before morph support existed.
    /// </summary>
    public static IReadOnlyList<float>? ResolveWeights(
        Node node, Animation? animation, float time)
    {
        var mesh = node.Mesh;
        if (mesh == null || !HasMorphTargets(mesh)) return null;

        // An animated weights channel supersedes the static defaults entirely;
        // glTF does not blend the two.
        var weights = SampleAnimatedWeights(node, animation, time)
                      ?? StaticWeights(node, mesh);

        return weights.Any(Contributes) ? weights : null;
    }

    /// <summary>
    ///     Returns <c>base + sum(weight_i * delta_i)</c> for one vertex attribute,
    ///     or null when no target contributes to it. Targets are allowed to omit
    ///     an attribute the base mesh has (the GBA exporter writes POSITION deltas
    ///     only), and a null return leaves that attribute untouched.
    /// </summary>
    public static Vector3[]? Apply(MeshPrimitive primitive, string attribute,
        IReadOnlyList<Vector3> baseValues, IReadOnlyList<float> weights)
    {
        Vector3[]? morphed = null;
        var targetCount = Math.Min(primitive.MorphTargetsCount, weights.Count);

        for (var target = 0; target < targetCount; target++)
        {
            var weight = weights[target];
            if (!Contributes(weight)) continue;

            if (!primitive.GetMorphTargetAccessors(target)
                    .TryGetValue(attribute, out var accessor))
                continue;

            var deltas = accessor.AsVector3Array();
            var count = Math.Min(deltas.Count, baseValues.Count);
            if (count == 0) continue;

            morphed ??= [.. baseValues];
            for (var i = 0; i < count; i++)
                morphed[i] += deltas[i] * weight;
        }

        return morphed;
    }

    /// <summary>
    ///     Exact rather than tolerant: a zero weight contributes nothing, and any
    ///     other value — however small — is a contribution the clip authored.
    /// </summary>
    private static bool Contributes(float weight)
    {
        return Math.Abs(weight) > 0f;
    }

    private static bool HasMorphTargets(Mesh mesh)
    {
        return mesh.Primitives.Any(static primitive => primitive.MorphTargetsCount > 0);
    }

    private static float[]? SampleAnimatedWeights(
        Node node, Animation? animation, float time)
    {
        var channel = animation?.FindMorphChannel(node);
        if (channel == null) return null;

        // The curve sampler honours the channel's own interpolation mode —
        // LINEAR, STEP and CUBICSPLINE alike — and clamps outside the key range,
        // matching how the node-TRS path treats times past the last key.
        return channel.GetMorphSampler().CreateCurveSampler(false).GetPoint(time);
    }

    private static IReadOnlyList<float> StaticWeights(Node node, Mesh mesh)
    {
        var nodeWeights = node.GetMorphWeights();
        return nodeWeights.Count > 0 ? nodeWeights : mesh.GetMorphWeights();
    }
}
