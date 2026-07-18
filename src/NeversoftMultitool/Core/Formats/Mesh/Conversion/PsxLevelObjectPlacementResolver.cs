using System.Globalization;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves authored instances from a Spider-Man level's trigger data into
///     the model bank stored by <c>*_o.psx</c>. The object file is not an
///     environment layer: AUTOEXEC spools it for <c>V_MODEL_CHECKSUM</c>
///     lookups, while only <c>*_g.psx</c> is attached as environment geometry.
/// </summary>
internal static class PsxLevelObjectPlacementResolver
{
    private const int PlatformSubType = 0x192;
    private const string ModelChecksumOpcode = "0x212F";
    private const float PsxAngleUnitsPerRevolution = 4096f;

    internal static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Resolve(
        AssetSource source,
        string geometryFileName,
        PsxMeshFile objectBank)
    {
        if (!TryGetLevelStem(geometryFileName, out var levelStem))
            return EmptyPlacements;

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
                return EmptyPlacements;

            using var stream = new MemoryStream(triggerBytes, writable: false);
            using var reader = new BinaryReader(stream);
            var trg = TrgFile.Parse(reader, levelStem + "_t.trg");
            return Resolve(trg, objectBank);
        }
        catch (Exception ex)
        {
            // Object-bank placement is optional preview enrichment. A bad TRG
            // must not prevent the selected geometry layer from opening.
            System.Diagnostics.Debug.WriteLine(
                $"Unable to resolve optional PSX object placements: {ex.Message}");
            return EmptyPlacements;
        }
    }

    internal static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>> Resolve(
        TrgFile trg,
        PsxMeshFile objectBank)
    {
        if (!trg.IsSpiderMan || objectBank.Objects.Count == 0)
            return EmptyPlacements;

        // Spool_GetModel resolves the model checksum to a model/mesh index.
        // Associate that definition with one object-table entry solely so the
        // existing geometry writer can emit it; never use that entry's stored
        // position, which belongs to the bank and is not a runtime instance.
        var objectIndexByHash = new Dictionary<uint, int>();
        for (var objectIndex = 0; objectIndex < objectBank.Objects.Count; objectIndex++)
        {
            var meshIndex = objectBank.Objects[objectIndex].MeshIndex;
            if (meshIndex >= objectBank.MeshNameHashes.Length)
                continue;

            objectIndexByHash.TryAdd(objectBank.MeshNameHashes[meshIndex], objectIndex);
        }

        var placements = new Dictionary<int, List<PsxLevelObjectPlacement>>();
        // Include both level-start and event-created PLATFORM nodes. This is an
        // authored overview for a static viewer, not a simulation of one
        // particular runtime frame; omitting suspended/event nodes would again
        // remove structural pieces such as the prototype l1a2 upper bank.
        foreach (var node in trg.Nodes)
        {
            if (node.SubType != PlatformSubType
                || node.Position == null
                || node.Angles == null
                || node.Script == null)
            {
                continue;
            }

            var checksum = FindAuthoredDefaultChecksum(node.Script);

            // A static preview cannot evaluate game state, so prefer an
            // unconditional assignment. Some scripts put it before an IF and
            // others after ENDIF; branch alternatives can become visibility
            // groups in a future pass.
            if (checksum == 0
                || !objectIndexByHash.TryGetValue(checksum, out var objectIndex))
            {
                continue;
            }

            if (!placements.TryGetValue(objectIndex, out var objectPlacements))
            {
                objectPlacements = [];
                placements.Add(objectIndex, objectPlacements);
            }

            objectPlacements.Add(new PsxLevelObjectPlacement(
                node.Index,
                CreateGltfTransform(
                    node.Position,
                    node.Angles,
                    objectBank.TranslationDivisor)));
        }

        return placements.ToDictionary(
            static pair => pair.Key,
            static pair => (IReadOnlyList<PsxLevelObjectPlacement>)pair.Value
                .OrderBy(static placement => placement.TriggerNodeIndex)
                .ToArray());
    }

    private static Matrix4x4 CreateGltfTransform(
        TrgPosition position,
        TrgAngles angles,
        float translationDivisor)
    {
        if (!float.IsFinite(translationDivisor) || translationDivisor <= 0f)
            translationDivisor = 1f;

        var nativeRotation = CreateNativeYxzRotation(angles);
        var gltfRotation = Quaternion.Normalize(new Quaternion(
            nativeRotation.X,
            -nativeRotation.Y,
            -nativeRotation.Z,
            nativeRotation.W));
        var nativePosition = new Vector3(
            position.RawX / translationDivisor,
            position.RawY / translationDivisor,
            position.RawZ / translationDivisor);

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

    private static bool TryGetLevelStem(string fileName, out string levelStem)
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

    private static IReadOnlyDictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>
        EmptyPlacements { get; } =
            new Dictionary<int, IReadOnlyList<PsxLevelObjectPlacement>>();
}

internal readonly record struct PsxLevelObjectPlacement(
    int TriggerNodeIndex,
    Matrix4x4 Transform);
