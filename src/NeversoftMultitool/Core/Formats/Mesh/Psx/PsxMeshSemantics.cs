using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

internal static class PsxMeshSemantics
{
    internal const ushort StitchSourceType = 0x0001;
    internal const ushort StitchedReferenceType = 0x0002;

    internal static bool IsExactStitchSource(ushort type)
    {
        return type == StitchSourceType;
    }

    internal static bool IsExactStitchedReference(ushort type)
    {
        return type == StitchedReferenceType;
    }

    internal static Vector3 GetObjectOffset(PsxMeshObject obj, float translationDivisor)
    {
        return new Vector3(
            obj.X(translationDivisor),
            obj.Y(translationDivisor),
            obj.Z(translationDivisor));
    }

    internal static Vector3 GetObjectOffset(PsxMeshFile psxFile, PsxMeshObject obj)
    {
        return GetObjectOffset(obj, psxFile.TranslationDivisor);
    }

    internal static bool UsesCharacterObjectOrder(PsxMeshFile psxFile)
    {
        // Characters render positionally: RenderSuperItem draws ppModels[part]
        // with part == slot, 1:1 (decomp char_mesh_selection.md), and
        // M3dInit_ParsePSX (decomp-VERIFIED, psx_model_load_format.md §1)
        // fills ppModels contiguously with no index remap. That applies to
        // HIER-table files AND to flat stitched supers (THPS1-proto hawk,
        // Apocalypse bruce): on those, obj.MeshIndex is limb-permuted
        // relative to the part slots (hawk: hand<->bicep, 12<->14) and
        // binding through it hangs the bicep mesh on the wrist pivot — the
        // source of the proto arm/leg garble. obj.MeshIndex is an
        // object/item-table field (load-format doc §6.2) meaningful only for
        // item lookup on non-character files, which keep it via the fallback
        // (as do count-mismatched files, where positional cannot apply).
        // HIER counts only for supers: level files carry HIER chunks for
        // their placed animated objects and are not part-ordered characters.
        return (psxFile.HasHierarchy && psxFile.IsSuperModel) ||
               (psxFile.HasStitchedReferences &&
                psxFile.Objects.Count == psxFile.Meshes.Count);
    }

    internal static int GetCharacterMeshIndex(PsxMeshFile psxFile, int objectIndex)
    {
        if (objectIndex < 0)
            return -1;

        if (UsesCharacterObjectOrder(psxFile))
            return objectIndex < psxFile.Meshes.Count ? objectIndex : -1;

        if (objectIndex >= psxFile.Objects.Count)
            return -1;

        var meshIndex = psxFile.Objects[objectIndex].MeshIndex;
        return meshIndex < psxFile.Meshes.Count ? meshIndex : -1;
    }

    internal static int GetCharacterObjectIndex(PsxMeshFile psxFile, int meshIndex)
    {
        if (meshIndex < 0)
            return -1;

        if (UsesCharacterObjectOrder(psxFile))
            return meshIndex < psxFile.Objects.Count ? meshIndex : -1;

        return meshIndex < psxFile.MeshToObjectIndex.Count
            ? psxFile.MeshToObjectIndex[meshIndex]
            : -1;
    }

    internal static Vector3 GetCharacterObjectOffset(PsxMeshFile psxFile, int meshIndex)
    {
        var objectIndex = GetCharacterObjectIndex(psxFile, meshIndex);
        if (objectIndex < 0 || objectIndex >= psxFile.Objects.Count)
            return Vector3.Zero;

        return GetObjectOffset(psxFile, psxFile.Objects[objectIndex]);
    }

    internal static HashSet<int> FindAlternateLeafObjectIndices(PsxMeshFile psxFile)
    {
        return FindAlternateLeafObjectGroups(psxFile)
            .Select(static group => group.AlternateObjectIndex)
            .ToHashSet();
    }

    /// <summary>
    ///     Finds conservative, pairwise geometry alternatives at a character
    ///     leaf placement. Keeping the default/alternate relationship (rather
    ///     than only returning the discarded leaves) lets callers expose the
    ///     runtime-style choice without making either mesh unrecoverable.
    /// </summary>
    internal static IReadOnlyList<PsxAlternateLeafGroup> FindAlternateLeafObjectGroups(
        PsxMeshFile psxFile)
    {
        var groups = new List<PsxAlternateLeafGroup>();
        if (!psxFile.HasHierarchy || psxFile.Objects.Count == 0)
            return groups;

        var hasChild = new bool[psxFile.Objects.Count];
        for (var i = 0; i < psxFile.Objects.Count; i++)
        {
            var parent = psxFile.Objects[i].ParentIndex;
            if (parent >= 0 && parent < hasChild.Length && parent != i)
                hasChild[parent] = true;
        }

        var leavesByPlacement = new Dictionary<AlternateLeafKey, List<int>>();
        for (var objectIndex = 0; objectIndex < psxFile.Objects.Count; objectIndex++)
        {
            if (hasChild[objectIndex])
                continue;

            var obj = psxFile.Objects[objectIndex];
            if (obj.ParentIndex < 0)
                continue;

            var meshIndex = GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
                continue;

            var mesh = psxFile.Meshes[meshIndex];
            if (mesh.Faces.Count == 0)
                continue;

            var key = new AlternateLeafKey(obj.ParentIndex, obj.RawX, obj.RawY, obj.RawZ);
            if (!leavesByPlacement.TryGetValue(key, out var leaves))
            {
                leaves = [];
                leavesByPlacement.Add(key, leaves);
            }

            leaves.Add(objectIndex);
        }

        foreach (var leaves in leavesByPlacement.Values)
        {
            if (leaves.Count < 2)
                continue;

            // The PS1 v4 and PC/DC v6 super formats both place simultaneous
            // parts at identical parent-local pivots. A placement-only rule
            // mistakes Spider-Man PC's controller pieces, VMU shell, and Ock
            // spline controllers for visibility variants. Use topology plus
            // bounding-envelope evidence for both understood layouts, while
            // retaining the legacy behavior for older, uncovered revisions.
            if (psxFile.Version is not (0x04 or 0x06))
            {
                for (var leafIndex = 1; leafIndex < leaves.Count; leafIndex++)
                    groups.Add(CreateAlternateLeafGroup(psxFile, leaves[0], leaves[leafIndex]));
                continue;
            }

            // THPS2 character-customization supers store four unstitched,
            // texture-bearing head variants at one placement. Accept that
            // narrow shape only when every leaf is an almost coincident
            // topology peer of the first.
            if (IsTightlyOverlappingUnstitchedFourLeafGroup(psxFile, leaves))
            {
                for (var leafIndex = 1; leafIndex < leaves.Count; leafIndex++)
                    groups.Add(CreateAlternateLeafGroup(psxFile, leaves[0], leaves[leafIndex]));
                continue;
            }

            var retainedLeaves = new List<int> { leaves[0] };
            for (var leafIndex = 1; leafIndex < leaves.Count; leafIndex++)
            {
                var objectIndex = leaves[leafIndex];

                // Equal parent/pivot is necessary but not sufficient. True
                // character alternatives (Spider-Man's open/closed hand
                // meshes) are topology peers at the same joint and occupy the
                // same world-space envelope. Distinct articulated parts can
                // deliberately share a pivot while extending in opposite
                // directions: control.psx models both controller grips and
                // both thumbsticks this way. The old placement-only test
                // discarded one member of each pair.
                var defaultObjectIndex = retainedLeaves.FirstOrDefault(retainedObjectIndex =>
                    IsSupportedAlternatePair(
                        psxFile,
                        retainedObjectIndex,
                        objectIndex,
                        allowTwoLeafFallback: leaves.Count == 2), -1);
                if (defaultObjectIndex >= 0)
                {
                    groups.Add(CreateAlternateLeafGroup(
                        psxFile, defaultObjectIndex, objectIndex));
                }
                else
                {
                    retainedLeaves.Add(objectIndex);
                }
            }
        }

        return groups;
    }

    private static PsxAlternateLeafGroup CreateAlternateLeafGroup(
        PsxMeshFile psxFile,
        int defaultObjectIndex,
        int alternateObjectIndex)
    {
        return new PsxAlternateLeafGroup(
            defaultObjectIndex,
            alternateObjectIndex,
            psxFile.Objects[defaultObjectIndex].ParentIndex);
    }

    private static bool IsSupportedAlternatePair(
        PsxMeshFile psxFile,
        int firstObjectIndex,
        int candidateObjectIndex,
        bool allowTwoLeafFallback)
    {
        var firstIsStitched = HasStitchedReference(psxFile, firstObjectIndex);
        var candidateIsStitched = HasStitchedReference(psxFile, candidateObjectIndex);
        var bothAreStitched = firstIsStitched && candidateIsStitched;
        var isMixedPair = allowTwoLeafFallback && firstIsStitched != candidateIsStitched;
        if (bothAreStitched)
        {
            // A stitched leaf is a terminal character part joined to its
            // parent seam. Two such leaves at exactly the same joint are pose
            // alternatives even when one deliberately changes silhouette and
            // topology. Carnage's ordinary hands (20 vertices / 25 faces) and
            // axe hands (27 / 40) are the canonical case: a topology-similarity
            // gate incorrectly emitted both sets at once. Requiring envelope
            // overlap still protects coincident pivots whose geometry extends
            // in genuinely different directions.
            return HaveWorldBoundsOverlap(
                psxFile, firstObjectIndex, candidateObjectIndex, minimumOverlap: 0.5f);
        }

        if (allowTwoLeafFallback && !firstIsStitched && !candidateIsStitched)
        {
            // Some visible leaf alternatives are self-contained rather than
            // seam-stitched. THPS2's low-detail skin_head/baldblack_head pair
            // and Enter Electro's two mercenary-gun variants are the corpus
            // examples: both members are textured topology peers occupying
            // essentially the same envelope. Keep this fallback restricted
            // to two-leaf groups and textured geometry. In particular, Ock's
            // coincident untextured controller boxes are four simultaneous
            // seven-joint spline chains, not render alternatives.
            return HasTexturedFaces(psxFile, firstObjectIndex)
                   && HasTexturedFaces(psxFile, candidateObjectIndex)
                   && HaveSimilarTopologyAndOverlap(
                       psxFile,
                       firstObjectIndex,
                       candidateObjectIndex,
                       minimumOverlap: 0.75f);
        }

        if (!isMixedPair)
            return false;

        return HaveSimilarTopologyAndOverlap(
            psxFile, firstObjectIndex, candidateObjectIndex, minimumOverlap: 0.5f);
    }

    private static bool HasTexturedFaces(PsxMeshFile psxFile, int objectIndex)
    {
        var meshIndex = GetCharacterMeshIndex(psxFile, objectIndex);
        return meshIndex >= 0
               && meshIndex < psxFile.Meshes.Count
               && psxFile.Meshes[meshIndex].Faces.Any(
                   static face => face.IsTextured && face.TextureHash != 0);
    }

    private static bool IsTightlyOverlappingUnstitchedFourLeafGroup(
        PsxMeshFile psxFile,
        List<int> leaves)
    {
        if (leaves.Count != 4)
            return false;

        var firstMeshIndex = GetCharacterMeshIndex(psxFile, leaves[0]);
        if (firstMeshIndex < 0 || firstMeshIndex >= psxFile.Meshes.Count)
            return false;

        var vertexCount = psxFile.Meshes[firstMeshIndex].Vertices.Count;
        foreach (var objectIndex in leaves)
        {
            var meshIndex = GetCharacterMeshIndex(psxFile, objectIndex);
            if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
                return false;

            var mesh = psxFile.Meshes[meshIndex];
            if (mesh.Vertices.Count != vertexCount
                || HasStitchedReference(mesh)
                || !mesh.Faces.Any(face => face.IsTextured && face.TextureHash != 0))
            {
                return false;
            }
        }

        return leaves.Skip(1).All(candidateObjectIndex =>
            HaveSimilarTopologyAndOverlap(
                psxFile, leaves[0], candidateObjectIndex, minimumOverlap: 0.95f));
    }

    private static bool HasStitchedReference(PsxMeshFile psxFile, int objectIndex)
    {
        var meshIndex = GetCharacterMeshIndex(psxFile, objectIndex);
        return meshIndex >= 0
               && meshIndex < psxFile.Meshes.Count
               && HasStitchedReference(psxFile.Meshes[meshIndex]);
    }

    private static bool HasStitchedReference(PsxMesh mesh)
    {
        return mesh.Vertices.Any(vertex => IsExactStitchedReference(vertex.Type));
    }

    private static bool HaveSimilarTopologyAndOverlap(
        PsxMeshFile psxFile,
        int firstObjectIndex,
        int candidateObjectIndex,
        float minimumOverlap)
    {
        var firstMeshIndex = GetCharacterMeshIndex(psxFile, firstObjectIndex);
        var candidateMeshIndex = GetCharacterMeshIndex(psxFile, candidateObjectIndex);
        if (firstMeshIndex < 0 || firstMeshIndex >= psxFile.Meshes.Count
            || candidateMeshIndex < 0 || candidateMeshIndex >= psxFile.Meshes.Count)
        {
            return false;
        }

        var firstMesh = psxFile.Meshes[firstMeshIndex];
        var candidateMesh = psxFile.Meshes[candidateMeshIndex];

        if (!HaveSimilarTopology(firstMesh.Vertices.Count, candidateMesh.Vertices.Count)
            || !HaveSimilarTopology(firstMesh.Faces.Count, candidateMesh.Faces.Count))
        {
            return false;
        }

        return HaveWorldBoundsOverlap(
            psxFile, firstObjectIndex, candidateObjectIndex, minimumOverlap);
    }

    private static bool HaveWorldBoundsOverlap(
        PsxMeshFile psxFile,
        int firstObjectIndex,
        int candidateObjectIndex,
        float minimumOverlap)
    {
        var firstMeshIndex = GetCharacterMeshIndex(psxFile, firstObjectIndex);
        var candidateMeshIndex = GetCharacterMeshIndex(psxFile, candidateObjectIndex);
        if (firstMeshIndex < 0 || firstMeshIndex >= psxFile.Meshes.Count
            || candidateMeshIndex < 0 || candidateMeshIndex >= psxFile.Meshes.Count
            || !TryGetWorldBounds(psxFile, firstMeshIndex, out var firstBounds)
            || !TryGetWorldBounds(psxFile, candidateMeshIndex, out var candidateBounds))
        {
            return false;
        }

        var intersection = Vector3.Min(firstBounds.Max, candidateBounds.Max)
                           - Vector3.Max(firstBounds.Min, candidateBounds.Min);
        if (intersection.X <= 0f || intersection.Y <= 0f || intersection.Z <= 0f)
            return false;

        var firstSize = firstBounds.Max - firstBounds.Min;
        var candidateSize = candidateBounds.Max - candidateBounds.Min;
        var firstVolume = firstSize.X * firstSize.Y * firstSize.Z;
        var candidateVolume = candidateSize.X * candidateSize.Y * candidateSize.Z;
        var smallerVolume = MathF.Min(firstVolume, candidateVolume);
        if (smallerVolume <= float.Epsilon)
            return false;

        var intersectionVolume = intersection.X * intersection.Y * intersection.Z;
        return intersectionVolume / smallerVolume >= minimumOverlap;
    }

    private static bool HaveSimilarTopology(int firstCount, int candidateCount)
    {
        var largerCount = Math.Max(firstCount, candidateCount);
        return largerCount > 0
               && (float)Math.Min(firstCount, candidateCount) / largerCount >= 0.75f;
    }

    private static bool TryGetWorldBounds(
        PsxMeshFile psxFile,
        int meshIndex,
        out PsxBounds bounds)
    {
        bounds = default;
        if (meshIndex < 0 || meshIndex >= psxFile.Meshes.Count)
            return false;

        var vertices = psxFile.Meshes[meshIndex].Vertices;
        if (vertices.Count == 0)
            return false;

        var min = new Vector3(float.PositiveInfinity);
        var max = new Vector3(float.NegativeInfinity);
        for (var vertexIndex = 0; vertexIndex < vertices.Count; vertexIndex++)
        {
            var position = PsxCharacterMeshResolver.ResolveVertex(
                psxFile, meshIndex, (uint)vertexIndex).WorldPosition;
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        bounds = new PsxBounds(min, max);
        return true;
    }

    internal static Vector3 ToGltfPosition(Vector3 position)
    {
        return new Vector3(position.X, -position.Y, -position.Z);
    }

    private readonly record struct AlternateLeafKey(int ParentIndex, int RawX, int RawY, int RawZ);

    private readonly record struct PsxBounds(Vector3 Min, Vector3 Max);
}

internal readonly record struct PsxAlternateLeafGroup(
    int DefaultObjectIndex,
    int AlternateObjectIndex,
    int ParentObjectIndex);
