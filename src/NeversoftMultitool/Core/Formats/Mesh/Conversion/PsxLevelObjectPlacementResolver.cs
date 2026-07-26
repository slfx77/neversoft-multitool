using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves world placements for the model bank stored by <c>*_o.psx</c>.
///     The bank's object table is itself a placed layer: its stored positions
///     are authored world instances (the same convention DDM level assembly
///     reads from PSX layout entries), verified corpus-wide by
///     <c>tools/diagnostics/psx_level_object_survey.py</c> — TRG
///     <c>V_MODEL_CHECKSUM</c> nodes reference only a fraction of every bank
///     (final l2a2: 4 of 63; several levels: zero), and where a platform node
///     references a model at its home position the node's coordinates coincide
///     with the bank entry's. PLATFORM trigger nodes therefore OVERLAY the bank
///     layer with scripted re-instances (elevators, repeats, event objects)
///     rather than being the sole placement source.
/// </summary>
internal static class PsxLevelObjectPlacementResolver
{
    private const int PlatformSubType = 0x192;
    private const string ModelChecksumOpcode = "0x212F";
    private const float PsxAngleUnitsPerRevolution = 4096f;

    /// <summary>Placeholder trigger index for the bank's own instances.</summary>
    internal const int BankInstanceNodeIndex = -1;

    /// <summary>
    ///     World-unit tolerance separating "the node references the bank's own
    ///     instance" (observed deltas ≤ ~9 units) from a genuine re-instance
    ///     (observed deltas ≥ ~400 units).
    /// </summary>
    private const float CoincidenceToleranceWorldUnits = 16f;

    private static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        EmptyPlacements { get; } =
        new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>();

    internal static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Resolve(
        AssetSource source,
        string geometryFileName,
        PsxMeshFile objectBank)
    {
        if (!TryGetLevelStem(geometryFileName, out var levelStem))
            return EmptyPlacements;

        return Resolve(TryLoadTriggerCompanion(source, levelStem), objectBank);
    }

    internal static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Resolve(
        TrgFile? trg,
        PsxMeshFile objectBank,
        bool applyTriggerOverlay = true)
    {
        if (objectBank.Objects.Count == 0)
            return EmptyPlacements;

        // Base layer: every renderable bank object at its stored position.
        var placements = new Dictionary<int, List<PsxLevelObjectPlacement>>();
        var bankWorldPositions = new Dictionary<int, Vector3>();
        for (var objectIndex = 0; objectIndex < objectBank.Objects.Count; objectIndex++)
        {
            var obj = objectBank.Objects[objectIndex];
            if (obj.MeshIndex >= objectBank.Meshes.Count)
                continue;

            var worldPosition = PsxMeshSemantics.GetObjectOffset(objectBank, obj);
            bankWorldPositions[objectIndex] = worldPosition;
            placements[objectIndex] =
            [
                new PsxLevelObjectPlacement(
                    BankInstanceNodeIndex,
                    Matrix4x4.CreateTranslation(
                        PsxMeshSemantics.ToGltfPosition(worldPosition)))
            ];
        }

        // All TRG generations overlay their PLATFORM/MANIPOB model references on
        // the bank layer identically: Spider-Man (v2.1), THPS1/THPS2 and
        // Apocalypse (v2.0) share the node record shape (subtype 0x192, opcode
        // 0x212F, position at the bank's div 2.25) and the same coincidence
        // semantics — a node at a bank instance's position replaces it, an
        // off-bank node adds a re-instance. Coincidence verified for Spider-Man
        // and THPS (THPS1 24/30, THPS2 12/17 at δ≈0); Apocalypse references are
        // mostly re-instances (authored BADDY/PLATFORM spawns) that stay in the
        // level bounds at the same node scale. The applyTriggerOverlay flag is the
        // per-caller knob (see MeshCompanionResolver.PsxLevelCompanions).
        if (trg != null && applyTriggerOverlay)
            OverlayTriggerInstances(trg, objectBank, placements, bankWorldPositions);

        return placements.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<PsxLevelObjectPlacement>)pair.Value
                .OrderBy(static placement => placement.TriggerNodeIndex)
                .ToArray());
    }

    /// <summary>
    ///     Loads the sibling <c>*_t.trg</c> for a level stem, tolerating a
    ///     missing or malformed file (returns null). Shared by the bank overlay
    ///     and the POWERUP placement layer so the TRG parses once per level.
    /// </summary>
    internal static TrgFile? TryLoadTriggerCompanion(AssetSource source, string levelStem)
    {
        try
        {
            byte[]? triggerBytes = null;
            foreach (var companionName in GetTriggerCompanionNames(levelStem))
            {
                triggerBytes = source.TryReadCompanion(companionName);
                if (triggerBytes != null)
                    break;
            }

            if (triggerBytes == null)
                return null;

            using var stream = new MemoryStream(triggerBytes, false);
            using var reader = new BinaryReader(stream);
            return TrgFile.Parse(reader, levelStem + "_t.trg");
        }
        catch (Exception ex)
        {
            // Trigger data only enriches the level. A bad TRG must not remove
            // the geometry or the bank's own objects.
            Debug.WriteLine(
                $"Unable to resolve optional PSX trigger placements: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Builds the glTF transform for a translation-only TRG node instance
    ///     (POWERUP pickups carry no rotation). Same world-space convention as
    ///     the platform overlay's <see cref="CreateGltfTransform" />. When a
    ///     <paramref name="terrain" /> and a <paramref name="pickupHoverWorldUnits" />
    ///     are supplied (a grounded POWERUP), the native origin is snapped to
    ///     <c>groundY - hover</c> before the glTF basis flip — the engine's exact
    ///     runtime ground-snap (<c>CPowerUp</c> → <c>Utils_GetGroundHeight</c>).
    /// </summary>
    internal static Matrix4x4 CreateNodeTranslation(
        TrgPosition position,
        float translationDivisor,
        PsxTerrainHeightField? terrain = null,
        float? pickupHoverWorldUnits = null)
    {
        var nativePosition = GetNodeWorldPosition(position, translationDivisor);
        if (terrain != null && pickupHoverWorldUnits is { } hover)
            nativePosition = PsxGroundSnap.SnapPickupToGround(
                terrain, nativePosition, translationDivisor, hover);

        return Matrix4x4.CreateTranslation(PsxMeshSemantics.ToGltfPosition(nativePosition));
    }

    /// <summary>
    ///     Adds entity-node instances on top of the bank layer: PLATFORM nodes
    ///     (model checksum in the script) and MANIPOB nodes (model checksums in
    ///     the node record — chairs, plants, papers; the repeats). Includes both
    ///     level-start and event-created nodes: this is an authored overview for
    ///     a static viewer, not a simulation of one runtime frame. A node whose
    ///     position coincides with a bank instance of the same model IS that
    ///     object — its placement (which can carry an authored rotation the
    ///     bank lacks) replaces the bank one instead of doubling it. Models that
    ///     appear only as MANIPOB alternate/damage states start hidden in-game,
    ///     so their bank home instance is removed (they otherwise z-fight,
    ///     stacked on the intact variant).
    /// </summary>
    private static void OverlayTriggerInstances(
        TrgFile trg,
        PsxMeshFile objectBank,
        Dictionary<int, List<PsxLevelObjectPlacement>> placements,
        Dictionary<int, Vector3> bankWorldPositions)
    {
        var objectIndicesByHash = new Dictionary<uint, List<int>>();
        for (var objectIndex = 0; objectIndex < objectBank.Objects.Count; objectIndex++)
        {
            var meshIndex = objectBank.Objects[objectIndex].MeshIndex;
            if (meshIndex >= objectBank.MeshNameHashes.Length)
                continue;

            var hash = objectBank.MeshNameHashes[meshIndex];
            if (!objectIndicesByHash.TryGetValue(hash, out var indices))
            {
                indices = [];
                objectIndicesByHash.Add(hash, indices);
            }

            indices.Add(objectIndex);
        }

        var alternateStateChecksums = new HashSet<uint>();
        var instancedChecksums = new HashSet<uint>();
        foreach (var node in trg.Nodes)
        {
            if (node.AlternateModelChecksums != null)
            {
                foreach (var alternate in node.AlternateModelChecksums)
                    alternateStateChecksums.Add(alternate);
            }

            var checksum = GetNodeModelChecksum(node);
            if (checksum == 0
                || node.Position == null
                || node.Angles == null
                || !objectIndicesByHash.TryGetValue(checksum, out var objectIndices))
            {
                continue;
            }

            instancedChecksums.Add(checksum);
            var nodeWorldPosition = GetNodeWorldPosition(
                node.Position, objectBank.TranslationDivisor);
            var nodePlacement = new PsxLevelObjectPlacement(
                node.Index,
                CreateGltfTransform(
                    node.Position,
                    node.Angles,
                    objectBank.TranslationDivisor));

            var coincidentObjectIndex = objectIndices
                .Where(objectIndex =>
                    bankWorldPositions.TryGetValue(objectIndex, out var bankPosition)
                    && Vector3.Distance(bankPosition, nodeWorldPosition)
                    <= CoincidenceToleranceWorldUnits)
                .Cast<int?>()
                .FirstOrDefault();
            if (coincidentObjectIndex is { } coincident)
            {
                var objectPlacements = placements[coincident];
                var bankSlot = objectPlacements.FindIndex(static placement =>
                    placement.TriggerNodeIndex == BankInstanceNodeIndex);
                if (bankSlot >= 0)
                    objectPlacements[bankSlot] = nodePlacement;
                else
                    objectPlacements.Add(nodePlacement);
                continue;
            }

            if (!placements.TryGetValue(objectIndices[0], out var rePlacements))
                continue;

            rePlacements.Add(nodePlacement);
        }

        RemoveAlternateStateBankInstances(
            placements, objectIndicesByHash, alternateStateChecksums, instancedChecksums);
    }

    /// <summary>
    ///     Drops the bank home instance of models that are referenced only as
    ///     MANIPOB alternate/damage states — events swap them in at runtime, so
    ///     at level start they are hidden. A model that is also instanced in
    ///     its own right keeps its placements.
    /// </summary>
    private static void RemoveAlternateStateBankInstances(
        Dictionary<int, List<PsxLevelObjectPlacement>> placements,
        Dictionary<uint, List<int>> objectIndicesByHash,
        HashSet<uint> alternateStateChecksums,
        HashSet<uint> instancedChecksums)
    {
        foreach (var checksum in alternateStateChecksums)
        {
            if (instancedChecksums.Contains(checksum)
                || !objectIndicesByHash.TryGetValue(checksum, out var objectIndices))
            {
                continue;
            }

            foreach (var objectIndex in objectIndices)
            {
                if (!placements.TryGetValue(objectIndex, out var objectPlacements))
                    continue;

                objectPlacements.RemoveAll(static placement => placement.TriggerNodeIndex == BankInstanceNodeIndex);
                if (objectPlacements.Count == 0)
                    placements.Remove(objectIndex);
            }
        }
    }

    /// <summary>
    ///     The model a trigger node instances: PLATFORM nodes carry it in their
    ///     script (V_MODEL_CHECKSUM, preferring an unconditional assignment — a
    ///     static preview cannot evaluate game state; branch alternatives can
    ///     become visibility groups in a future pass); MANIPOB nodes carry it
    ///     in the parsed node record.
    /// </summary>
    private static uint GetNodeModelChecksum(TrgNode node)
    {
        if (node is { SubType: PlatformSubType, Script: not null })
            return FindAuthoredDefaultChecksum(node.Script);

        return node.ModelChecksum ?? 0;
    }

    private static Vector3 GetNodeWorldPosition(
        TrgPosition position,
        float translationDivisor)
    {
        if (!float.IsFinite(translationDivisor) || translationDivisor <= 0f)
            translationDivisor = 1f;

        return new Vector3(
            position.RawX / translationDivisor,
            position.RawY / translationDivisor,
            position.RawZ / translationDivisor);
    }

    private static Matrix4x4 CreateGltfTransform(
        TrgPosition position,
        TrgAngles angles,
        float translationDivisor)
    {
        var nativeRotation = CreateNativeYxzRotation(angles);
        var gltfRotation = Quaternion.Normalize(new Quaternion(
            nativeRotation.X,
            -nativeRotation.Y,
            -nativeRotation.Z,
            nativeRotation.W));
        var nativePosition = GetNodeWorldPosition(position, translationDivisor);

        var transform = Matrix4x4.CreateFromQuaternion(gltfRotation);
        transform.Translation = PsxMeshSemantics.ToGltfPosition(nativePosition);
        return transform;
    }

    private static Quaternion CreateNativeYxzRotation(TrgAngles angles)
    {
        var rx = ToRadians(angles.RawX);
        var ry = ToRadians(angles.RawY);
        var rz = ToRadians(angles.RawZ);
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, rx);
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ry);
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, rz);
        return Quaternion.Normalize(qy * qx * qz);
    }

    private static float ToRadians(short angle)
    {
        return (angle & 0x0fff) * (2f * MathF.PI / PsxAngleUnitsPerRevolution);
    }

    private static bool TryReadChecksum(object? value, out uint checksum)
    {
        switch (value)
        {
            case uint uintValue:
                checksum = uintValue;
                return true;
            case int intValue when intValue >= 0:
                checksum = (uint)intValue;
                return true;
            case string text:
                var span = text.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    span = span[2..];
                return uint.TryParse(
                    span,
                    NumberStyles.AllowHexSpecifier,
                    CultureInfo.InvariantCulture,
                    out checksum);
            default:
                checksum = 0;
                return false;
        }
    }

    private static uint FindAuthoredDefaultChecksum(IReadOnlyList<TrgScriptOp> script)
    {
        uint firstConditionalChecksum = 0;
        var conditionalDepth = 0;
        foreach (var op in script)
        {
            if (IsConditionalStart(op.Opcode))
            {
                conditionalDepth++;
                continue;
            }

            if (op.Opcode == "0x4120")
            {
                conditionalDepth = Math.Max(0, conditionalDepth - 1);
                continue;
            }

            if (op.Opcode != ModelChecksumOpcode
                || !TryReadChecksum(op.Value, out var checksum)
                || checksum == 0)
            {
                continue;
            }

            if (conditionalDepth == 0)
                return checksum;
            if (firstConditionalChecksum == 0)
                firstConditionalChecksum = checksum;
        }

        // A branch-only assignment has no normal/default model, but dropping
        // it would make authored/event geometry unavailable in this static
        // overview. Keep its first model until conditional object variants can
        // be represented as dedicated visibility groups.
        return firstConditionalChecksum;
    }

    private static bool IsConditionalStart(string opcode)
    {
        return opcode is "0x4112" or "0x4113" or "0x4114" or
            "0x4115" or "0x4116" or "0x4117" or "0x4118" or
            "0x4119";
    }

    internal static bool TryGetLevelStem(string fileName, out string levelStem)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        if (!stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase)
            || stem.Length <= 2)
        {
            levelStem = string.Empty;
            return false;
        }

        levelStem = stem[..^2];
        return true;
    }

    private static IEnumerable<string> GetTriggerCompanionNames(string levelStem)
    {
        yield return levelStem + "_t.trg";
        yield return levelStem + "_T.trg";
        yield return levelStem + "_t.TRG";
        yield return levelStem + "_T.TRG";
        yield return levelStem.ToLowerInvariant() + "_t.trg";
        yield return levelStem.ToUpperInvariant() + "_T.TRG";
    }
}
