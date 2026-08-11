using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Finds opaque bank faces that become coplanar overlays only after a PSX
///     object bank is placed into its level. The regular detector works in one
///     <see cref="PsxMeshFile" /> at a time; this adapter builds a temporary
///     writer-equivalent comparison scope and delegates every geometric and
///     appearance decision back to that detector.
/// </summary>
internal static class PsxPlacedCoplanarOverlayResolver
{
    private static readonly PsxPlacedCoplanarOverlayResult Empty = new(
        new Dictionary<PsxPlacedFaceInstanceKey, PsxCoplanarOverlayAssignment>(),
        [],
        []);

    /// <summary>
    ///     Compares static level objects with the supplied, already-filtered
    ///     bank placements. Only a face selected on the BANK side is returned:
    ///     level geometry has already been emitted when the optional bank is
    ///     resolved, while a bank-selected overlay can still be split in its
    ///     own placement-aware writer pass.
    ///
    ///     Exact duplicate transforms are classified once and expanded back to
    ///     their original placement indices. This prevents duplicate instances
    ///     from changing the detector's component/rank graph while retaining a
    ///     distinct writer key for every emitted node. Camera-locked sky objects
    ///     are excluded because they do not coexist with the level in world
    ///     space.
    /// </summary>
    internal static PsxPlacedCoplanarOverlayResult FindBankOverlays(
        PsxMeshFile level,
        PsxMeshFile bank,
        IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> placements,
        IReadOnlySet<int>? excludedBankObjectIndices = null,
        IReadOnlySet<int>? excludedLevelObjectIndices = null)
    {
        if (placements.Count == 0 || level.Objects.Count == 0 || bank.Objects.Count == 0)
            return Empty;

        var scope = new ScopeBuilder();
        for (var objectIndex = 0; objectIndex < level.Objects.Count; objectIndex++)
        {
            if (excludedLevelObjectIndices?.Contains(objectIndex) == true)
                continue;

            var obj = level.Objects[objectIndex];
            if (obj.MeshIndex >= level.Meshes.Count)
                continue;

            var transform = Matrix4x4.CreateTranslation(
                PsxMeshSemantics.ToGltfPosition(PsxMeshSemantics.GetObjectOffset(level, obj)));
            if (!scope.TryAdd(
                    level,
                    level.Meshes[obj.MeshIndex],
                    transform,
                    new ScopeInstance(ScopeLayer.Level, objectIndex, [])))
            {
                return Empty;
            }
        }

        var bankInstances = 0;
        foreach (var (objectIndex, objectPlacements) in placements.OrderBy(static pair => pair.Key))
        {
            if (excludedBankObjectIndices?.Contains(objectIndex) == true
                || (uint)objectIndex >= (uint)bank.Objects.Count)
            {
                continue;
            }

            var obj = bank.Objects[objectIndex];
            if (obj.MeshIndex >= bank.Meshes.Count)
                continue;

            foreach (var uniquePlacement in DistinctTransforms(objectPlacements))
            {
                if (!scope.TryAdd(
                        bank,
                        bank.Meshes[obj.MeshIndex],
                        uniquePlacement.Transform,
                        new ScopeInstance(
                            ScopeLayer.Bank,
                            objectIndex,
                            uniquePlacement.PlacementIndices)))
                {
                    return Empty;
                }

                bankInstances++;
            }
        }

        if (bankInstances == 0)
            return Empty;

        var assembled = scope.Build(level.Version);
        var selected = new Dictionary<(int SyntheticObjectIndex, int FaceIndex),
            List<PsxCrossFileCoplanarPair>>();
        var detectedPairs = new List<PsxCrossFileCoplanarDetection>();
        foreach (var diagnostic in PsxCoplanarOverlayDetector.DiagnosePairs(assembled))
        {
            if (diagnostic.Overlay is not { } overlay)
                continue;

            var first = scope.Instances[diagnostic.First.ObjectIndex];
            var second = scope.Instances[diagnostic.Second.ObjectIndex];
            if (first.Layer == second.Layer)
                continue;

            var levelKey = first.Layer == ScopeLayer.Level
                ? diagnostic.First
                : diagnostic.Second;
            var bankKey = first.Layer == ScopeLayer.Bank
                ? diagnostic.First
                : diagnostic.Second;
            var levelInstance = scope.Instances[levelKey.ObjectIndex];
            var bankInstance = scope.Instances[bankKey.ObjectIndex];
            var overlayInstance = scope.Instances[overlay.ObjectIndex];
            foreach (var placementIndex in bankInstance.PlacementIndices)
            {
                detectedPairs.Add(new PsxCrossFileCoplanarDetection(
                    new PsxFaceInstanceKey(levelInstance.SourceObjectIndex, levelKey.FaceIndex),
                    new PsxPlacedFaceInstanceKey(
                        bankInstance.SourceObjectIndex,
                        placementIndex,
                        bankKey.FaceIndex),
                    overlayInstance.Layer == ScopeLayer.Bank));
            }

            if (overlayInstance.Layer != ScopeLayer.Bank)
                continue;

            var selectedKey = (overlay.ObjectIndex, overlay.FaceIndex);
            if (!selected.TryGetValue(selectedKey, out var pairs))
            {
                pairs = [];
                selected.Add(selectedKey, pairs);
            }

            foreach (var placementIndex in overlayInstance.PlacementIndices)
            {
                pairs.Add(new PsxCrossFileCoplanarPair(
                    new PsxFaceInstanceKey(levelInstance.SourceObjectIndex, levelKey.FaceIndex),
                    new PsxPlacedFaceInstanceKey(
                        overlayInstance.SourceObjectIndex,
                        placementIndex,
                        overlay.FaceIndex),
                    diagnostic.SharedAreaFraction ?? 0f,
                    diagnostic.AdmittedTriangleSharedAreaFraction ?? 0f,
                    diagnostic.AdmittedPlaneDistanceDelta ?? 0f));
            }
        }

        if (selected.Count == 0)
        {
            return detectedPairs.Count == 0
                ? Empty
                : new PsxPlacedCoplanarOverlayResult(
                    Empty.Assignments,
                    [],
                    detectedPairs.Distinct().ToArray());
        }

        var assignments = new Dictionary<PsxPlacedFaceInstanceKey, PsxCoplanarOverlayAssignment>();
        var acceptedPairs = new List<PsxCrossFileCoplanarPair>();
        var groupId = 0;
        foreach (var entry in selected
                     .OrderBy(static pair => pair.Key.SyntheticObjectIndex)
                     .ThenBy(static pair => pair.Key.FaceIndex))
        {
            foreach (var pair in entry.Value
                         .Distinct()
                         .OrderBy(static pair => pair.BankFace.ObjectIndex)
                         .ThenBy(static pair => pair.BankFace.PlacementIndex)
                         .ThenBy(static pair => pair.BankFace.FaceIndex)
                         .ThenBy(static pair => pair.LevelFace.ObjectIndex)
                         .ThenBy(static pair => pair.LevelFace.FaceIndex))
            {
                assignments.TryAdd(
                    pair.BankFace,
                    new PsxCoplanarOverlayAssignment(groupId, 1));
                acceptedPairs.Add(pair);
            }

            groupId++;
        }

        return new PsxPlacedCoplanarOverlayResult(
            assignments,
            acceptedPairs,
            detectedPairs.Distinct().ToArray());
    }

    private static IEnumerable<UniquePlacement> DistinctTransforms(
        IReadOnlyList<PsxLevelObjectPlacement> placements)
    {
        var unique = new List<(Matrix4x4 Transform, List<int> Indices)>();
        for (var placementIndex = 0; placementIndex < placements.Count; placementIndex++)
        {
            var transform = placements[placementIndex].Transform;
            var existing = unique.FindIndex(item => item.Transform.Equals(transform));
            if (existing >= 0)
            {
                unique[existing].Indices.Add(placementIndex);
                continue;
            }

            unique.Add((transform, [placementIndex]));
        }

        return unique.Select(static item =>
            new UniquePlacement(item.Transform, item.Indices.ToArray()));
    }

    private enum ScopeLayer
    {
        Level,
        Bank
    }

    private readonly record struct UniquePlacement(
        Matrix4x4 Transform,
        IReadOnlyList<int> PlacementIndices);

    private readonly record struct ScopeInstance(
        ScopeLayer Layer,
        int SourceObjectIndex,
        IReadOnlyList<int> PlacementIndices);

    /// <summary>
    ///     Builds a regular-vertex mesh for each assembled object instance.
    ///     Sprite corners are resolved BEFORE the instance transform, matching
    ///     <see cref="PsxGeometryWriter" />; transformed points are then encoded
    ///     back into the detector's (X,-Y,-Z) basis. Consequently rotations
    ///     preserve the emitted triangle winding and its admitting primary or
    ///     secondary plane rather than approximating a placement as translation.
    /// </summary>
    private sealed class ScopeBuilder
    {
        private readonly List<PsxMeshObject> _objects = [];
        private readonly List<PsxMesh> _meshes = [];
        private readonly Dictionary<RenderedAppearance, uint> _appearanceIds = [];
        private readonly Dictionary<PsxMeshFile, uint> _sourceFileIds =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<PsxTextureWibble, uint> _textureWibbleIds =
            new(TextureWibbleComparer.Instance);

        internal List<ScopeInstance> Instances { get; } = [];

        internal bool TryAdd(
            PsxMeshFile sourceFile,
            PsxMesh source,
            Matrix4x4 transform,
            ScopeInstance instance)
        {
            if (source.Faces.Count == 0)
                return true;
            if (_meshes.Count >= ushort.MaxValue)
                return false;

            var spriteResolver = PsxSpriteVertexResolver.TryCreate(source);
            var vertices = new List<PsxVertex>(source.Vertices.Count);
            for (var vertexIndex = 0; vertexIndex < source.Vertices.Count; vertexIndex++)
            {
                var sourceVertex = source.Vertices[vertexIndex];
                var local = spriteResolver != null
                            && spriteResolver.TryResolvePosition((uint)vertexIndex, out var spriteCorner)
                    ? spriteCorner
                    : new Vector3(sourceVertex.X, -sourceVertex.Y, -sourceVertex.Z);
                var world = Vector3.Transform(local, transform);
                vertices.Add(new PsxVertex
                {
                    X = world.X,
                    Y = -world.Y,
                    Z = -world.Z,
                    // The writer-expanded point is now an ordinary vertex.
                    // Keeping the source sprite flag would expand it twice.
                    Type = 0
                });
            }

            var meshIndex = _meshes.Count;
            _meshes.Add(new PsxMesh
            {
                Flags = source.Flags,
                Vertices = vertices,
                Normals = [],
                Faces = source.Faces
                    .Select(face => CloneWithCanonicalAppearance(sourceFile, source, face))
                    .ToList(),
                VertexCount = (uint)vertices.Count
            });
            _objects.Add(new PsxMeshObject { MeshIndex = (ushort)meshIndex });
            Instances.Add(instance);
            return true;
        }

        /// <summary>
        ///     The single-file detector can compare raw Gouraud palette indices
        ///     because every candidate shares one palette. An assembled scope
        ///     cannot: index 7 in the level and index 7 in the bank may resolve
        ///     to different colours, while two different indices may resolve to
        ///     the same colour. Replace the four raw colour bytes with one shared
        ///     32-bit identity for the actually rendered corner-colour tuple.
        ///     Pulsed palette entries also retain a conservative (file, index)
        ///     token because the baked frame-zero colour does not describe their
        ///     later frames. Texture-wibble structure participates for the same
        ///     reason. The detector's existing raw equality then retains its
        ///     exact-twin semantics without learning any cross-file special case.
        /// </summary>
        private PsxFace CloneWithCanonicalAppearance(
            PsxMeshFile sourceFile,
            PsxMesh sourceMesh,
            PsxFace sourceFace)
        {
            var colors = PsxGeometryHelpers.ComputePsxFaceColors(
                sourceFile.Version,
                sourceMesh,
                sourceFace,
                sourceFile.GouraudPalette);
            var appearance = new RenderedAppearance(
                colors.C0,
                colors.C1,
                colors.C2,
                colors.C3,
                // For a Gouraud quad Mode is the fourth palette index and its
                // resolved C3 already captures it. Otherwise it is a packet
                // mode byte, retained as a non-colour appearance discriminator.
                sourceFace.IsGouraud && sourceFace.IsQuad
                    ? (byte)0
                    : sourceFace.Mode,
                GetPulseIdentity(sourceFile, sourceFace, sourceFace.R),
                GetPulseIdentity(sourceFile, sourceFace, sourceFace.G),
                GetPulseIdentity(sourceFile, sourceFace, sourceFace.B),
                GetPulseIdentity(
                    sourceFile,
                    sourceFace,
                    sourceFace.IsQuad ? sourceFace.Mode : sourceFace.R),
                GetTextureWibbleIdentity(sourceFace.TextureWibble),
                sourceFace.GetTextureCoordinate(0),
                sourceFace.GetTextureCoordinate(1),
                sourceFace.GetTextureCoordinate(2),
                sourceFace.GetTextureCoordinate(3));
            if (!_appearanceIds.TryGetValue(appearance, out var appearanceId))
            {
                appearanceId = (uint)_appearanceIds.Count;
                _appearanceIds.Add(appearance, appearanceId);
            }

            return new PsxFace
            {
                Flags = sourceFace.Flags,
                IsQuad = sourceFace.IsQuad,
                IsTextured = sourceFace.IsTextured,
                IsGouraud = sourceFace.IsGouraud,
                IsSemiTransparent = sourceFace.IsSemiTransparent,
                Index0 = sourceFace.Index0,
                Index1 = sourceFace.Index1,
                Index2 = sourceFace.Index2,
                Index3 = sourceFace.Index3,
                NormalIndex = sourceFace.NormalIndex,
                R = (byte)appearanceId,
                G = (byte)(appearanceId >> 8),
                B = (byte)(appearanceId >> 16),
                Mode = (byte)(appearanceId >> 24),
                TextureHash = sourceFace.TextureHash,
                // The appearance identity above owns the authoritative widened
                // coordinates. Normalize legacy bytes so v6 placeholders or
                // clamping cannot independently split or merge exact twins.
                U0 = 0,
                V0 = 0,
                U1 = 0,
                V1 = 0,
                U2 = 0,
                V2 = 0,
                U3 = 0,
                V3 = 0,
                TextureWibble = sourceFace.TextureWibble,
                TextureCoordinates =
                [
                    sourceFace.GetTextureCoordinate(0),
                    sourceFace.GetTextureCoordinate(1),
                    sourceFace.GetTextureCoordinate(2),
                    sourceFace.GetTextureCoordinate(3)
                ]
            };
        }

        private PulseIdentity GetPulseIdentity(
            PsxMeshFile sourceFile,
            PsxFace sourceFace,
            byte paletteIndex)
        {
            if (!sourceFace.IsGouraud
                || !sourceFile.ColourPulses.Any(pulse => pulse.ColourIndex == paletteIndex))
            {
                return default;
            }

            if (!_sourceFileIds.TryGetValue(sourceFile, out var sourceFileId))
            {
                sourceFileId = (uint)_sourceFileIds.Count + 1;
                _sourceFileIds.Add(sourceFile, sourceFileId);
            }

            return new PulseIdentity(sourceFileId, paletteIndex);
        }

        private uint GetTextureWibbleIdentity(PsxTextureWibble? wibble)
        {
            if (wibble == null)
                return 0;

            if (!_textureWibbleIds.TryGetValue(wibble, out var identity))
            {
                identity = (uint)_textureWibbleIds.Count + 1;
                _textureWibbleIds.Add(wibble, identity);
            }

            return identity;
        }

        internal PsxMeshFile Build(ushort version)
        {
            return new PsxMeshFile
            {
                Version = version,
                Objects = _objects,
                Meshes = _meshes,
                MeshNameHashes = new uint[_meshes.Count],
                TextureHashes = [],
                ScaleDivisor = 1f,
                TranslationDivisor = 1f
            };
        }

        private readonly record struct RenderedAppearance(
            Vector4 C0,
            Vector4 C1,
            Vector4 C2,
            Vector4 C3,
            byte NonColourMode,
            PulseIdentity Pulse0,
            PulseIdentity Pulse1,
            PulseIdentity Pulse2,
            PulseIdentity Pulse3,
            uint TextureWibbleIdentity,
            PsxTextureCoordinate UV0,
            PsxTextureCoordinate UV1,
            PsxTextureCoordinate UV2,
            PsxTextureCoordinate UV3);

        private readonly record struct PulseIdentity(uint SourceFileId, byte PaletteIndex);

        private sealed class TextureWibbleComparer : IEqualityComparer<PsxTextureWibble>
        {
            internal static TextureWibbleComparer Instance { get; } = new();

            public bool Equals(PsxTextureWibble? first, PsxTextureWibble? second)
            {
                if (ReferenceEquals(first, second))
                    return true;
                if (first == null || second == null)
                    return false;

                return first.UVelocity == second.UVelocity
                       && first.VVelocity == second.VVelocity
                       && first.Frequency == second.Frequency
                       && first.ZeroUAmplitudes == second.ZeroUAmplitudes
                       && first.ZeroVAmplitudes == second.ZeroVAmplitudes
                       && first.UsesFaceTextureCoordinates == second.UsesFaceTextureCoordinates
                       && first.Vertices.AsSpan().SequenceEqual(second.Vertices);
            }

            public int GetHashCode(PsxTextureWibble value)
            {
                var hash = new HashCode();
                hash.Add(value.UVelocity);
                hash.Add(value.VVelocity);
                hash.Add(value.Frequency);
                hash.Add(value.ZeroUAmplitudes);
                hash.Add(value.ZeroVAmplitudes);
                hash.Add(value.UsesFaceTextureCoordinates);
                foreach (var vertex in value.Vertices)
                    hash.Add(vertex);
                return hash.ToHashCode();
            }
        }
    }
}

/// <summary>
///     Placement-aware overlay key. A source face is deliberately insufficient:
///     the same bank object may be emitted at several transforms and overlap the
///     level at only one of them.
/// </summary>
internal readonly record struct PsxPlacedFaceInstanceKey(
    int ObjectIndex,
    int PlacementIndex,
    int FaceIndex);

internal readonly record struct PsxCrossFileCoplanarPair(
    PsxFaceInstanceKey LevelFace,
    PsxPlacedFaceInstanceKey BankFace,
    float SharedAreaFraction,
    float AdmittedTriangleSharedAreaFraction,
    float PlaneDistanceDelta);

internal readonly record struct PsxCrossFileCoplanarDetection(
    PsxFaceInstanceKey LevelFace,
    PsxPlacedFaceInstanceKey BankFace,
    bool BankFaceSelected);

internal sealed record PsxPlacedCoplanarOverlayResult(
    IReadOnlyDictionary<PsxPlacedFaceInstanceKey, PsxCoplanarOverlayAssignment> Assignments,
    IReadOnlyList<PsxCrossFileCoplanarPair> AcceptedPairs,
    IReadOnlyList<PsxCrossFileCoplanarDetection> DetectedPairs);
