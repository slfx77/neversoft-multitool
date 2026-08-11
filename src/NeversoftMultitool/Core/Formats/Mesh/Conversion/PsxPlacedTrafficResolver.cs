using System.Numerics;
using NeversoftMultitool.Core.Formats.Animation;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves the independently-spooled CSuper models used by PSX traffic
///     BADDYs. Traffic models are selected by the game constructor from the
///     BADDY subtype; they are not objects or animation streams in the level
///     geometry file. Script-created records describe potential spawn
///     snapshots only: they do not reproduce trigger timing, repeated spawns,
///     or movement along the linked road network.
/// </summary>
internal static class PsxPlacedTrafficResolver
{
    internal const int TaxiSubType = 0xD5;
    internal const int PoliceSubType = 0xD6;
    internal const int VanSubType = 0xD7;
    internal const int CableCarSubType = 0xD8;
    internal const int KartSubType = 0xD9;
    internal const int MarSubType = 0xDA;

    private const int RuntimeRoadYOffset = -110;
    private const float PsxAngleUnitsPerRevolution = 4096f;
    private const byte CreateAtLevelStartFlag = 1;
    private const byte KeepOnActiveListFlag = 2;

    private static IReadOnlyList<PsxPlacedTrafficPlacement> Empty { get; } =
        Array.Empty<PsxPlacedTrafficPlacement>();

    /// <summary>
    ///     Returns one placement per usable traffic BADDY. Missing or malformed
    ///     nodes and model companions are skipped independently so an optional
    ///     traffic source can never suppress the rest of the level.
    /// </summary>
    internal static IReadOnlyList<PsxPlacedTrafficPlacement> Resolve(
        AssetSource levelSource,
        TrgFile? trg,
        float levelTranslationDivisor)
    {
        ArgumentNullException.ThrowIfNull(levelSource);
        if (trg == null
            || trg.Nodes.Count == 0
            || !float.IsFinite(levelTranslationDivisor)
            || levelTranslationDivisor <= 0f)
        {
            return Empty;
        }

        var nodesByIndex = BuildNodeIndex(trg.Nodes);
        var directlyPulsedNodes = BuildDirectCommandPointCreationTargets(trg.Nodes);
        var sourcesBySubType = new Dictionary<int, PsxPlacedTrafficSource?>();
        var placements = new List<PsxPlacedTrafficPlacement>();

        foreach (var node in trg.Nodes)
        {
            if (node.TypeId != TrgNodeMetadata.TypeBaddy
                || node.SubType is not { } subType
                || !TryGetPrimaryCompanionName(subType, out _)
                || !IsActiveWhenCreated(node.BaddyFlags)
                || node.Links is not { Count: > 0 }
                || node.Angles == null)
            {
                continue;
            }

            var initiallyCreated =
                node.BaddyFlags!.Contains(CreateAtLevelStartFlag);
            if (!initiallyCreated && !directlyPulsedNodes.Contains(node.Index))
                continue;

            var roadNodeIndex = node.Links[0];
            if (!nodesByIndex.TryGetValue(roadNodeIndex, out var roadNode)
                || roadNode.Position == null)
            {
                continue;
            }

            if (!sourcesBySubType.TryGetValue(subType, out var trafficSource))
            {
                trafficSource = TryLoadSource(levelSource, subType);
                // Cache failures as well as successes. A missing optional file
                // should be probed once, not once per car in a busy level.
                sourcesBySubType[subType] = trafficSource;
            }

            if (trafficSource == null)
                continue;

            placements.Add(new PsxPlacedTrafficPlacement(
                node.Index,
                roadNodeIndex,
                subType,
                initiallyCreated,
                CreateRootTransform(
                    roadNode.Position,
                    node.Angles,
                    levelTranslationDivisor),
                trafficSource));
        }

        return placements.Count == 0 ? Empty : placements;
    }

    /// <summary>Flag 2 keeps a created object out of SuspendedList.</summary>
    private static bool IsActiveWhenCreated(List<byte>? flags)
    {
        return flags?.Contains(KeepOnActiveListFlag) == true;
    }

    /// <summary>
    ///     Finds direct COMMANDPOINT links whose command list initializes one
    ///     pulse and then sends it. Execution reaches Trig_SendPulseToNode for
    ///     every linked node, which is the corpus-proven scripted CCar path.
    /// </summary>
    private static HashSet<int> BuildDirectCommandPointCreationTargets(
        List<TrgNode> nodes)
    {
        var result = new HashSet<int>();
        foreach (var node in nodes)
        {
            if (node.TypeId != TrgNodeMetadata.TypeCommandPoint
                || node.Links is not { Count: > 0 }
                || !HasOnePulseSend(node.Commands))
            {
                continue;
            }

            result.UnionWith(node.Links);
        }

        return result;
    }

    private static bool HasOnePulseSend(List<TrgCommand>? commands)
    {
        if (commands == null)
            return false;

        bool? sendsOnePulse = null;
        foreach (var command in commands)
        {
            if (command.Opcode == 0x86)
            {
                // The runtime honors only the first setup after creating the
                // command point; later 0x86 commands cannot correct it.
                sendsOnePulse ??= command.Args is [ushort pulseCount]
                                  && pulseCount == 1;
            }
            else if (sendsOnePulse == true && command.Opcode == 3)
            {
                return true;
            }
        }

        return false;
    }

    private static Dictionary<int, TrgNode> BuildNodeIndex(List<TrgNode> nodes)
    {
        var result = new Dictionary<int, TrgNode>(nodes.Count);
        foreach (var node in nodes)
            result.TryAdd(node.Index, node);
        return result;
    }

    private static PsxPlacedTrafficSource? TryLoadSource(
        AssetSource levelSource,
        int subType)
    {
        if (!TryGetPrimaryCompanionName(subType, out var companionName))
            return null;

        byte[]? bytes;
        try
        {
            bytes = levelSource.TryReadCompanion(companionName);
        }
        catch
        {
            return null;
        }

        // The THPS1 prototype predates the c_ prefix. This is the only
        // constructor/source-name fallback proven by the corpus; do not infer
        // equivalent aliases for the other traffic types.
        if (bytes == null && subType == TaxiSubType)
        {
            companionName = "taxi.psx";
            try
            {
                bytes = levelSource.TryReadCompanion(companionName);
            }
            catch
            {
                return null;
            }
        }

        if (bytes == null)
            return null;

        try
        {
            var meshFile = PsxMeshFile.Parse(bytes);
            if (meshFile is not { IsSuperModel: true }
                || meshFile.Objects.Count == 0)
            {
                return null;
            }

            var bank = PsxAnimationBank.TryProbe(
                levelSource, bytes, meshFile.Objects.Count);
            if (bank == null
                || !bank.MatchesTargetBoneCount
                || bank.BoneCount != meshFile.Objects.Count
                || bank.AnimFile.ChunkTag is not (
                    PsxMeshFile.HierChunkV1Tag or PsxMeshFile.HierChunkV2Tag)
                || bank.AnimFile.Entries.Count == 0)
            {
                return null;
            }

            // CCar starts CycleAnim(0, +1). oneShot:false reproduces its wrap
            // behavior, including v1 tween expansion toward frame zero.
            var decoded = PsxAnimationBank.Decode(
                bank,
                meshFile.Objects.Count,
                [new PsxAnimationBankSelection(0, "anim_0")],
                oneShot: false);
            if (decoded.Animations.Count != 1)
                return null;

            var animation = decoded.Animations[0].Animation;
            if (animation.BoneCount != meshFile.Objects.Count)
                return null;

            return new PsxPlacedTrafficSource(
                companionName,
                bytes,
                meshFile,
                bank.AnimFile,
                animation);
        }
        catch
        {
            return null;
        }
    }

    private static bool TryGetPrimaryCompanionName(
        int subType,
        out string companionName)
    {
        companionName = subType switch
        {
            TaxiSubType => "c_taxi.psx",
            PoliceSubType => "c_police.psx",
            VanSubType => "c_van.psx",
            CableCarSubType => "c_cable.psx",
            KartSubType => "c_kart.psx",
            MarSubType => "c_mar.psx",
            _ => ""
        };
        return companionName.Length > 0;
    }

    private static Matrix4x4 CreateRootTransform(
        TrgPosition roadPosition,
        TrgAngles baddyAngles,
        float translationDivisor)
    {
        var nativePosition = new Vector3(
            roadPosition.RawX / translationDivisor,
            (roadPosition.RawY + RuntimeRoadYOffset) / translationDivisor,
            roadPosition.RawZ / translationDivisor);
        var nativeRotation = CreateNativeYxzRotation(baddyAngles);
        var gltfRotation = Quaternion.Normalize(new Quaternion(
            nativeRotation.X,
            -nativeRotation.Y,
            -nativeRotation.Z,
            nativeRotation.W));

        var transform = Matrix4x4.CreateFromQuaternion(gltfRotation);
        transform.Translation = PsxMeshSemantics.ToGltfPosition(nativePosition);
        return transform;
    }

    private static Quaternion CreateNativeYxzRotation(TrgAngles angles)
    {
        var qx = Quaternion.CreateFromAxisAngle(Vector3.UnitX, ToRadians(angles.RawX));
        var qy = Quaternion.CreateFromAxisAngle(Vector3.UnitY, ToRadians(angles.RawY));
        var qz = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, ToRadians(angles.RawZ));
        return Quaternion.Normalize(qy * qx * qz);
    }

    private static float ToRadians(short angle)
    {
        return (angle & 0x0fff) * (2f * MathF.PI / PsxAngleUnitsPerRevolution);
    }
}

/// <summary>A parsed, decoded traffic model shared by every placement of it.</summary>
internal sealed record PsxPlacedTrafficSource(
    string CompanionName,
    byte[] Bytes,
    PsxMeshFile MeshFile,
    PsxAnimFile AnimationFile,
    PsxAnimation Animation);

/// <summary>A traffic CSuper instance at its authored road spawn position.</summary>
internal sealed record PsxPlacedTrafficPlacement(
    int TriggerNodeIndex,
    int RoadNodeIndex,
    int SubType,
    bool InitiallyCreated,
    Matrix4x4 RootTransform,
    PsxPlacedTrafficSource Source);
