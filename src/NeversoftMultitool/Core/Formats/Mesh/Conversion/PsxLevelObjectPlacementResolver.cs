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
///     reads from PSX layout entries), verified corpus-wide: TRG
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
    private const string WhatIfConditionalOpcode = "0x4117";
    private const string ElseConditionalOpcode = "0x4122";
    private const string EndIfOpcode = "0x4120";
    private const string DisplayOnOpcode = "0x4203";
    private const string DisplayOffOpcode = "0x4204";
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

    private static IReadOnlySet<int> EmptyNodeIndices { get; } = new HashSet<int>();

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
    ///     <see cref="Resolve(TrgFile?, PsxMeshFile, bool)" /> plus the set of
    ///     "What If?"-gated node indices, so callers can gate those placements
    ///     behind an opt-in visibility group. Resolution itself is unchanged:
    ///     the authored overview keeps every scripted instance.
    /// </summary>
    internal static PsxLevelObjectPlacementSet ResolveDetailed(
        TrgFile? trg,
        PsxMeshFile objectBank,
        bool applyTriggerOverlay = true)
    {
        var placements = Resolve(trg, objectBank, applyTriggerOverlay);
        var whatIfNodeIndices = trg != null && applyTriggerOverlay && placements.Count > 0
            ? FindWhatIfGatedNodeIndices(trg)
            : EmptyNodeIndices;
        return new PsxLevelObjectPlacementSet(placements, whatIfNodeIndices);
    }

    /// <summary>
    ///     PLATFORM-overlay nodes whose prop only exists (or only displays)
    ///     when the "What If?" easter-egg mode is active. Bare co-occurrence
    ///     of <c>C_IF_WHAT_IF</c> (0x4117) and <c>V_MODEL_CHECKSUM</c> gated
    ///     81 corpus nodes whose PLACED model is unconditional (final l6a2's
    ///     scale-animated props set their model at depth 0 and only OVERRIDE
    ///     it inside the What If block; SM2EE's if/else grammar places the
    ///     ELSE-branch model) — fixed 2026-07-29 with an opener-tracked
    ///     conditional stack. A node is What If content iff EITHER the
    ///     checksum <see cref="FindAuthoredDefaultChecksum" /> selects for
    ///     placement is read while a 0x4117 block is open (l2a1's motorcycle
    ///     nodes: model exists only inside the block), OR the script is
    ///     display-off by default — an unconditional <c>C_DISPLAY_OFF</c>
    ///     with every <c>C_DISPLAY_ON</c> inside a 0x4117 block (final l1a3
    ///     node 322, l5a3 192/196/198, l8a5 29/50). Placements from these
    ///     nodes — including a coincidence-replaced bank instance, which
    ///     carries the node's index — are What If content.
    /// </summary>
    internal static IReadOnlySet<int> FindWhatIfGatedNodeIndices(TrgFile trg)
    {
        HashSet<int>? gated = null;
        foreach (var node in trg.Nodes)
        {
            if (node is not { SubType: PlatformSubType, Script: not null })
                continue;

            var facts = AnalyzePlatformScript(node.Script);
            if (!facts.HasWhatIfConditional || facts.SelectedChecksum == 0)
                continue;

            var displayOffByDefault = facts.HasUnconditionalDisplayOff
                                      && !facts.HasDisplayOnOutsideWhatIf;
            if (facts.SelectedUnderWhatIf || displayOffByDefault)
                (gated ??= []).Add(node.Index);
        }

        return gated ?? EmptyNodeIndices;
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
        return AnalyzePlatformScript(script).SelectedChecksum;
    }

    /// <summary>
    ///     One pass over a PLATFORM script with an opener-tracked conditional
    ///     stack (each open block remembers its opening opcode; 0x4120
    ///     C_ENDIF pops, underflow-guarded). Placement selection, in order:
    ///     first depth-0 checksum (the authored default), else the first
    ///     checksum read with NO 0x4117 open (SM2EE's if/else grammar
    ///     "0x4117 A 0x4120 0x4122 B 0x4120" places the else-branch B — the
    ///     model shown outside What If mode), else the first What If-gated
    ///     checksum (branch-only nodes like l2a1's motorcycle keep their
    ///     model rather than dropping authored geometry from this static
    ///     overview). Display facts feed the What If gate's
    ///     display-off-by-default rule.
    /// </summary>
    private static PlatformScriptFacts AnalyzePlatformScript(IReadOnlyList<TrgScriptOp> script)
    {
        uint unconditionalChecksum = 0;
        uint defaultConditionalChecksum = 0;
        uint whatIfChecksum = 0;
        var openers = new List<string>();
        var whatIfOpenCount = 0;
        var hasWhatIfConditional = false;
        var hasUnconditionalDisplayOff = false;
        var hasDisplayOnOutsideWhatIf = false;
        foreach (var op in script)
        {
            if (IsConditionalStart(op.Opcode))
            {
                openers.Add(op.Opcode);
                if (op.Opcode == WhatIfConditionalOpcode)
                {
                    whatIfOpenCount++;
                    hasWhatIfConditional = true;
                }

                continue;
            }

            switch (op.Opcode)
            {
                case EndIfOpcode:
                    if (openers.Count > 0)
                    {
                        if (openers[^1] == WhatIfConditionalOpcode)
                            whatIfOpenCount--;
                        openers.RemoveAt(openers.Count - 1);
                    }

                    continue;
                case DisplayOffOpcode:
                    hasUnconditionalDisplayOff |= openers.Count == 0;
                    continue;
                case DisplayOnOpcode:
                    hasDisplayOnOutsideWhatIf |= whatIfOpenCount == 0;
                    continue;
            }

            if (op.Opcode != ModelChecksumOpcode
                || !TryReadChecksum(op.Value, out var checksum)
                || checksum == 0)
            {
                continue;
            }

            if (openers.Count == 0)
            {
                if (unconditionalChecksum == 0)
                    unconditionalChecksum = checksum;
            }
            else if (whatIfOpenCount == 0)
            {
                if (defaultConditionalChecksum == 0)
                    defaultConditionalChecksum = checksum;
            }
            else if (whatIfChecksum == 0)
            {
                whatIfChecksum = checksum;
            }
        }

        var selected = whatIfChecksum;
        var selectedUnderWhatIf = whatIfChecksum != 0;
        if (unconditionalChecksum != 0)
        {
            selected = unconditionalChecksum;
            selectedUnderWhatIf = false;
        }
        else if (defaultConditionalChecksum != 0)
        {
            selected = defaultConditionalChecksum;
            selectedUnderWhatIf = false;
        }

        return new PlatformScriptFacts(
            selected,
            selectedUnderWhatIf,
            hasWhatIfConditional,
            hasUnconditionalDisplayOff,
            hasDisplayOnOutsideWhatIf);
    }

    private static bool IsConditionalStart(string opcode)
    {
        // 0x4122 opens the else block of the preceding C_IF_* (its body runs
        // only when the if condition failed) and closes with its own C_ENDIF.
        return opcode is "0x4112" or "0x4113" or "0x4114" or
            "0x4115" or "0x4116" or "0x4117" or "0x4118" or
            "0x4119" or ElseConditionalOpcode;
    }

    /// <summary>
    ///     Everything <see cref="AnalyzePlatformScript" /> learns in one walk:
    ///     the checksum placement uses, whether it was read inside an open
    ///     <c>C_IF_WHAT_IF</c> block, and the display-default facts the What
    ///     If gate needs.
    /// </summary>
    private readonly record struct PlatformScriptFacts(
        uint SelectedChecksum,
        bool SelectedUnderWhatIf,
        bool HasWhatIfConditional,
        bool HasUnconditionalDisplayOff,
        bool HasDisplayOnOutsideWhatIf);

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
