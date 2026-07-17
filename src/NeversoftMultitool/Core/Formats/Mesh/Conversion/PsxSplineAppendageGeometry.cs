using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Reconstructs Spider-Man's runtime spline appendages from the seven-box
///     controller chains stored in the character PSX. The boxes are editor and
///     animation controls, not render geometry. The game skins a tube through
///     their transforms and, for Ock, instances the sibling claw.psx at each
///     endpoint. Keeping the generated rings single-bone weighted to their
///     corresponding controls lets the ordinary exported animation channels
///     drive the reconstructed surface.
/// </summary>
internal static class PsxSplineAppendageGeometry
{
    private const uint ScorpionHookMeshHash = 0xAF6C87FE;
    private const int ControllersPerChain = 7;
    private const int TubeSides = 8;

    internal static IReadOnlyList<PsxSplineControllerChain> FindControllerChains(PsxMeshFile psxFile)
    {
        if (!PsxGeometryHelpers.UsesCombinedPsxCharacterAssembly(psxFile)
            || psxFile.Objects.Count < ControllersPerChain)
        {
            return [];
        }

        // Every known spline super stores its controller boxes as one terminal
        // run: seven for Scorpion's tail, or four groups of seven for Ock's
        // tentacles. Requiring that complete structural signature avoids
        // mistaking ordinary cube props (including control.psx) for splines.
        var firstController = psxFile.Objects.Count;
        while (firstController > 0 && IsControllerCube(psxFile, firstController - 1))
            firstController--;

        var controllerCount = psxFile.Objects.Count - firstController;
        if (controllerCount is not ControllersPerChain and not (4 * ControllersPerChain))
            return [];

        // A lone seven-box run is not sufficient by itself. The February
        // Spider-Man prototype's Lizard carries an otherwise matching,
        // abandoned editor rig that later builds remove; treating it as a
        // tail produces a pole through the character's waist. Scorpion's
        // actual one-chain spline is independently identified by the
        // authored hook-tip mesh that the runtime places at its endpoint.
        // Four-chain Ock rigs use the sibling claw.psx and remain identified
        // by their complete 4x7 structure.
        if (controllerCount == ControllersPerChain
            && !psxFile.MeshNameHashes.Contains(ScorpionHookMeshHash))
        {
            return [];
        }

        var chains = new List<PsxSplineControllerChain>(controllerCount / ControllersPerChain);
        for (var start = firstController; start < psxFile.Objects.Count; start += ControllersPerChain)
        {
            var objectIndices = Enumerable.Range(start, ControllersPerChain).ToArray();
            var centers = objectIndices
                .Select(index => PsxMeshSemantics.GetObjectOffset(psxFile, psxFile.Objects[index]))
                .ToArray();
            var distances = centers.Zip(centers.Skip(1), Vector3.Distance).ToArray();
            var average = distances.Average();
            if (average is < 20f or > 80f
                || distances.Any(distance => MathF.Abs(distance - average) > average * 0.08f))
            {
                return [];
            }

            chains.Add(new PsxSplineControllerChain(objectIndices, centers));
        }

        return chains;
    }

    internal static HashSet<int> BuildControllerObjectSet(
        IReadOnlyList<PsxSplineControllerChain> chains)
    {
        return chains.SelectMany(static chain => chain.ObjectIndices).ToHashSet();
    }

    internal static IReadOnlyDictionary<int, PsxSplineTipPlacement> FindEmbeddedTipPlacements(
        PsxMeshFile psxFile,
        IReadOnlyList<PsxSplineControllerChain> chains)
    {
        if (chains.Count != 1)
            return new Dictionary<int, PsxSplineTipPlacement>();

        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.MeshNameHashes.Length
                || psxFile.MeshNameHashes[meshIndex] != ScorpionHookMeshHash)
            {
                continue;
            }

            var chain = chains[0];
            return new Dictionary<int, PsxSplineTipPlacement>
            {
                [objectIndex] = CreateTipPlacement(chain)
            };
        }

        return new Dictionary<int, PsxSplineTipPlacement>();
    }

    internal static void AppendGeneratedTubes(
        IReadOnlyList<PsxSplineControllerChain> chains,
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences)
    {
        var isTail = chains.Count == 1;
        foreach (var chain in chains)
        {
            AppendTube(chain, isTail, vertices, indices, influences);
        }
    }

    private static bool IsControllerCube(PsxMeshFile psxFile, int objectIndex)
    {
        var meshIndex = PsxMeshSemantics.GetCharacterMeshIndex(psxFile, objectIndex);
        if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
            return false;

        var mesh = psxFile.Meshes[meshIndex];
        if (mesh.Vertices.Count != 8
            || mesh.Faces.Count != 6
            || mesh.Vertices.Any(static vertex => vertex.Type != 0)
            || mesh.Faces.Any(static face => !face.IsQuad || face.IsTextured))
        {
            return false;
        }

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        foreach (var vertex in mesh.Vertices)
        {
            var position = new Vector3(vertex.X, vertex.Y, vertex.Z);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        var size = max - min;
        var smallest = MathF.Min(size.X, MathF.Min(size.Y, size.Z));
        var largest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        return smallest >= 5f && largest <= 30f && smallest / largest >= 0.9f;
    }

    private static void AppendTube(
        PsxSplineControllerChain chain,
        bool taperTail,
        List<ModelVertex> vertices,
        List<int> indices,
        List<ModelBoneInfluences> influences)
    {
        var averageSegmentLength = chain.Centers
            .Zip(chain.Centers.Skip(1), Vector3.Distance)
            .Average();
        var baseRadius = Math.Clamp(averageSegmentLength / 10f, 3.25f, 4.75f);
        var samples = BuildSmoothedSamples(chain);
        var sampleCenters = samples.Select(static sample => sample.Center).ToArray();
        var rings = new ModelVertex[samples.Count][];

        for (var ringIndex = 0; ringIndex < samples.Count; ringIndex++)
        {
            var tangent = GetTangent(sampleCenters, ringIndex);
            var (normal, binormal) = BuildFrame(tangent);
            var radius = taperTail
                ? baseRadius * (1f - 0.45f * ringIndex / (samples.Count - 1f))
                : baseRadius;
            rings[ringIndex] = new ModelVertex[TubeSides];
            for (var side = 0; side < TubeSides; side++)
            {
                var angle = 2f * MathF.PI * side / TubeSides;
                var radial = normal * MathF.Cos(angle) + binormal * MathF.Sin(angle);
                rings[ringIndex][side] = new ModelVertex(
                    PsxMeshSemantics.ToGltfPosition(samples[ringIndex].Center + radial * radius),
                    Vector3.Normalize(PsxMeshSemantics.ToGltfPosition(radial)),
                    new Vector4(0.58f, 0.60f, 0.63f, 1f),
                    new Vector2(side / (float)TubeSides, ringIndex / (float)(samples.Count - 1)));
            }
        }

        for (var ringIndex = 0; ringIndex < rings.Length - 1; ringIndex++)
        {
            var firstInfluence = samples[ringIndex].Influence;
            var secondInfluence = samples[ringIndex + 1].Influence;
            for (var side = 0; side < TubeSides; side++)
            {
                var next = (side + 1) % TubeSides;
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    rings[ringIndex][side], firstInfluence,
                    rings[ringIndex][next], firstInfluence,
                    rings[ringIndex + 1][side], secondInfluence);
                ModelDocumentGeometryAdapter.AddSkinnedTriangle(
                    vertices, indices, influences,
                    rings[ringIndex][next], firstInfluence,
                    rings[ringIndex + 1][next], secondInfluence,
                    rings[ringIndex + 1][side], secondInfluence);
            }
        }
    }

    private static List<PsxSplineSample> BuildSmoothedSamples(PsxSplineControllerChain chain)
    {
        var samples = new List<PsxSplineSample>(2 * (chain.Centers.Count - 1) + 2)
        {
            new(chain.Centers[0], ModelBoneInfluences.Single(chain.ObjectIndices[0]))
        };

        // Chaikin corner cutting is deliberately used instead of inventing
        // extra animation controls: two positive-weight samples per authored
        // span bevel the seven-point polyline, and ordinary two-joint glTF
        // skinning keeps those samples between the same controller transforms.
        for (var index = 0; index < chain.Centers.Count - 1; index++)
        {
            foreach (var amount in new[] { 0.25f, 0.75f })
            {
                samples.Add(new PsxSplineSample(
                    Vector3.Lerp(chain.Centers[index], chain.Centers[index + 1], amount),
                    new ModelBoneInfluences(
                        chain.ObjectIndices[index],
                        chain.ObjectIndices[index + 1],
                        0,
                        0,
                        1f - amount,
                        amount,
                        0f,
                        0f)));
            }
        }

        var last = chain.Centers.Count - 1;
        samples.Add(new PsxSplineSample(
            chain.Centers[last], ModelBoneInfluences.Single(chain.ObjectIndices[last])));
        return samples;
    }

    internal static PsxSplineTipPlacement CreateTipPlacement(PsxSplineControllerChain chain)
    {
        var last = chain.Centers.Count - 1;
        var tangent = GetTangent(chain.Centers, last);
        var (normal, binormal) = BuildFrame(tangent);
        return new PsxSplineTipPlacement(
            chain.ObjectIndices[last], chain.Centers[last], tangent, normal, binormal);
    }

    private static Vector3 GetTangent(IReadOnlyList<Vector3> centers, int index)
    {
        var tangent = index switch
        {
            0 => centers[1] - centers[0],
            _ when index == centers.Count - 1 => centers[index] - centers[index - 1],
            _ => centers[index + 1] - centers[index - 1]
        };
        return Vector3.Normalize(tangent);
    }

    private static (Vector3 Normal, Vector3 Binormal) BuildFrame(Vector3 tangent)
    {
        var reference = MathF.Abs(Vector3.Dot(tangent, Vector3.UnitY)) < 0.9f
            ? Vector3.UnitY
            : Vector3.UnitX;
        var normal = Vector3.Normalize(Vector3.Cross(tangent, reference));
        return (normal, Vector3.Normalize(Vector3.Cross(tangent, normal)));
    }
}

internal sealed record PsxSplineControllerChain(
    IReadOnlyList<int> ObjectIndices,
    IReadOnlyList<Vector3> Centers);

internal readonly record struct PsxSplineSample(
    Vector3 Center,
    ModelBoneInfluences Influence);

internal readonly record struct PsxSplineTipPlacement(
    int JointIndex,
    Vector3 Center,
    Vector3 Tangent,
    Vector3 Normal,
    Vector3 Binormal)
{
    internal Vector3 TransformPosition(Vector3 local)
    {
        return Center + TransformDirection(local);
    }

    internal Vector3 TransformDirection(Vector3 local)
    {
        return Normal * local.X + Binormal * local.Y + Tangent * local.Z;
    }
}
