using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Scene;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skeleton;
using NeversoftMultitool.Core.Formats.XbxScene;
using ParsedXbxScene = NeversoftMultitool.Core.Formats.XbxScene.XbxScene;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;

internal static class ThawPs2SkinningTransfer
{
    public static Result Apply(Scene.Ps2Scene ps2Scene, ParsedXbxScene pcScene, Ps2Skeleton skeleton)
    {
        var pcLookup = BuildPcVertexLookup(pcScene);
        var totalVertexCount = 0;
        var skinnedVertexCount = 0;
        var remappedGroups = new List<Ps2MeshGroup>(ps2Scene.MeshGroups.Count);

        foreach (var group in ps2Scene.MeshGroups)
        {
            var remappedMeshes = new List<Ps2Mesh>(group.Meshes.Count);
            foreach (var mesh in group.Meshes)
            {
                var remappedVertices = new Ps2Vertex[mesh.Vertices.Length];
                for (var i = 0; i < mesh.Vertices.Length; i++)
                {
                    totalVertexCount++;
                    var sourceVertex = mesh.Vertices[i];
                    var remappedVertex = sourceVertex;

                    if (!pcLookup.TryGetValue(mesh.MaterialChecksum, out var materialVertices))
                    {
                        remappedVertices[i] = remappedVertex;
                        continue;
                    }

                    var key = QuantizedPositionKey.From(sourceVertex.Position);
                    if (!materialVertices.TryGetValue(key, out var candidates))
                    {
                        remappedVertices[i] = remappedVertex;
                        continue;
                    }

                    if (TrySelectSkinning(sourceVertex, candidates, skeleton, out var skinning))
                    {
                        remappedVertex = new Ps2Vertex(
                            sourceVertex.Position,
                            sourceVertex.Normal,
                            sourceVertex.R,
                            sourceVertex.G,
                            sourceVertex.B,
                            sourceVertex.A,
                            sourceVertex.U,
                            sourceVertex.V,
                            sourceVertex.HasNormal,
                            sourceVertex.HasColor,
                            sourceVertex.HasUV,
                            sourceVertex.IsStripRestart,
                            skinning.BoneIndex0,
                            skinning.BoneIndex1,
                            skinning.BoneIndex2,
                            skinning.BoneWeight0,
                            skinning.BoneWeight1,
                            skinning.BoneWeight2,
                            true);
                        skinnedVertexCount++;
                    }

                    remappedVertices[i] = remappedVertex;
                }

                remappedMeshes.Add(new Ps2Mesh
                {
                    Checksum = mesh.Checksum,
                    MaterialChecksum = mesh.MaterialChecksum,
                    MeshFlags = mesh.MeshFlags,
                    BoundingSphere = mesh.BoundingSphere,
                    StartsOnOddOutputSlot = mesh.StartsOnOddOutputSlot,
                    Vertices = remappedVertices
                });
            }

            remappedGroups.Add(new Ps2MeshGroup
            {
                Checksum = group.Checksum,
                Meshes = remappedMeshes
            });
        }

        return new Result(
            new Scene.Ps2Scene
            {
                MaterialVersion = ps2Scene.MaterialVersion,
                MeshVersion = ps2Scene.MeshVersion,
                VertexVersion = ps2Scene.VertexVersion,
                Materials = ps2Scene.Materials,
                MeshGroups = remappedGroups
            },
            skinnedVertexCount,
            totalVertexCount);
    }

    public static Result? TryApplyFromCompanion(Scene.Ps2Scene ps2Scene, string ps2SkinPath, Ps2Skeleton skeleton)
    {
        var companionPath = TryFindPcSkinCompanion(ps2SkinPath);
        if (companionPath is null)
            return null;

        return Apply(ps2Scene, ThawSceneFile.Parse(companionPath), skeleton);
    }

    private static Dictionary<uint, Dictionary<QuantizedPositionKey, List<XbxVertex>>> BuildPcVertexLookup(
        ParsedXbxScene pcScene)
    {
        var lookup = new Dictionary<uint, Dictionary<QuantizedPositionKey, List<XbxVertex>>>();
        foreach (var sector in pcScene.Sectors)
        {
            foreach (var mesh in sector.Meshes)
            {
                if (!lookup.TryGetValue(mesh.MaterialChecksum, out var materialVertices))
                {
                    materialVertices = [];
                    lookup[mesh.MaterialChecksum] = materialVertices;
                }

                foreach (var vertex in mesh.Vertices)
                {
                    if (!vertex.HasSkinData)
                        continue;

                    var key = QuantizedPositionKey.From(vertex.Position);
                    if (!materialVertices.TryGetValue(key, out var candidates))
                    {
                        candidates = [];
                        materialVertices[key] = candidates;
                    }

                    candidates.Add(vertex);
                }
            }
        }

        return lookup;
    }

    private static bool TrySelectSkinning(
        in Ps2Vertex ps2Vertex,
        IReadOnlyList<XbxVertex> candidates,
        Ps2Skeleton skeleton,
        out ReducedSkinning skinning)
    {
        var bestScore = float.NegativeInfinity;
        XbxVertex? bestCandidate = null;

        foreach (var candidate in candidates)
        {
            var score = 0f;
            if (ps2Vertex.HasNormal && candidate.HasNormal)
            {
                var ps2Normal = SafeNormalize(ps2Vertex.Normal);
                var pcNormal = SafeNormalize(candidate.Normal);
                score += Vector3.Dot(ps2Normal, pcNormal) * 8f;
            }

            if (ps2Vertex.HasUV)
            {
                var du = MathF.Abs(ps2Vertex.U - candidate.TexCoord.X);
                var dv = MathF.Abs(1f - ps2Vertex.V - candidate.TexCoord.Y);
                score -= du + dv;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestCandidate = candidate;
            }
        }

        if (bestCandidate is null)
        {
            skinning = default;
            return false;
        }

        return TryReduceSkinning(bestCandidate.Value, skeleton, out skinning);
    }

    private static bool TryReduceSkinning(XbxVertex source, Ps2Skeleton skeleton, out ReducedSkinning skinning)
    {
        var influences = new List<(int BoneIndex, float Weight)>(4);
        AddInfluence(influences, source.BoneIndex0, source.BoneWeight0, skeleton.Bones.Length);
        AddInfluence(influences, source.BoneIndex1, source.BoneWeight1, skeleton.Bones.Length);
        AddInfluence(influences, source.BoneIndex2, source.BoneWeight2, skeleton.Bones.Length);
        AddInfluence(influences, source.BoneIndex3, source.BoneWeight3, skeleton.Bones.Length);

        if (influences.Count == 0)
        {
            skinning = default;
            return false;
        }

        var reduced = influences
            .OrderByDescending(influence => influence.Weight)
            .Take(3)
            .ToArray();

        var total = reduced.Sum(influence => influence.Weight);
        if (total <= 0f)
        {
            skinning = default;
            return false;
        }

        skinning = new ReducedSkinning(
            reduced.ElementAtOrDefault(0).BoneIndex,
            reduced.ElementAtOrDefault(1).BoneIndex,
            reduced.ElementAtOrDefault(2).BoneIndex,
            reduced.ElementAtOrDefault(0).Weight / total,
            reduced.ElementAtOrDefault(1).Weight / total,
            reduced.ElementAtOrDefault(2).Weight / total);
        return true;
    }

    private static void AddInfluence(List<(int BoneIndex, float Weight)> influences, int boneIndex, float weight,
        int boneCount)
    {
        if ((uint)boneIndex >= (uint)boneCount || weight <= 0.0001f)
            return;

        influences.Add((boneIndex, weight));
    }

    private static Vector3 SafeNormalize(Vector3 value)
    {
        var length = value.Length();
        return length > 0.0001f ? value / length : Vector3.UnitY;
    }

    private static string? TryFindPcSkinCompanion(string ps2SkinPath)
    {
        var dir = Path.GetDirectoryName(ps2SkinPath);
        if (dir is null)
            return null;

        var stem = Path.GetFileName(ps2SkinPath);
        if (stem.EndsWith(".skin.ps2", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^".skin.ps2".Length];
        else
            stem = Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(stem));

        var directMatch = CompanionSearch.FindCompanion(dir, stem, [".skin.wpc", ".skin.xbx"], ["SKIN", "Models"]);
        if (directMatch != null)
            return directMatch;

        var ancestor = dir;
        for (var depth = 0; depth < 4 && ancestor != null; depth++)
        {
            var wpcMatch = Directory.EnumerateFiles(ancestor, stem + ".skin.wpc", SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.Equals(ps2SkinPath, StringComparison.OrdinalIgnoreCase));
            if (wpcMatch != null)
                return wpcMatch;

            var xbxMatch = Directory.EnumerateFiles(ancestor, stem + ".skin.xbx", SearchOption.AllDirectories)
                .FirstOrDefault(path => !path.Equals(ps2SkinPath, StringComparison.OrdinalIgnoreCase));
            if (xbxMatch != null)
                return xbxMatch;

            ancestor = Path.GetDirectoryName(ancestor);
        }

        return null;
    }

    internal sealed record Result(Scene.Ps2Scene Scene, int SkinnedVertexCount, int TotalVertexCount);

    private readonly record struct QuantizedPositionKey(int X, int Y, int Z)
    {
        private const float Scale = 1024f;

        public static QuantizedPositionKey From(Vector3 position)
        {
            return new QuantizedPositionKey(
                (int)MathF.Round(position.X * Scale),
                (int)MathF.Round(position.Y * Scale),
                (int)MathF.Round(position.Z * Scale));
        }
    }

    private readonly record struct ReducedSkinning(
        int BoneIndex0,
        int BoneIndex1,
        int BoneIndex2,
        float BoneWeight0,
        float BoneWeight1,
        float BoneWeight2);
}
