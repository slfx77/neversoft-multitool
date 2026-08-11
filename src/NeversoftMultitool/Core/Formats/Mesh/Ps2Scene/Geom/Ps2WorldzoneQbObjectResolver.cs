using System.Numerics;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Qb;
using QbKeyHash = NeversoftMultitool.Core.QbKey.QbKey;

namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;

/// <summary>
///     Resolves THAW worldzone object resources to authored QB NodeArray instances.
///     Object resources use a named .mqb entry followed immediately by a CRC-zero
///     .mdl and .mcol. The main worldzone QB stores both direct-inline object structs
///     and structs which inherit a compressed template through a bare QBKey member.
/// </summary>
internal static class Ps2WorldzoneQbObjectResolver
{
    internal const uint ModelQbTypeHash = 0x4BC1E85E; // QbKey(".mqb")
    internal const uint NodeQbTypeHash = 0x49875607; // QbKey(".nqb")
    internal const uint CollisionTypeHash = 0x6290993B; // QbKey(".mcol")

    private const int MaxValueDepth = 64;
    private const int MaxInheritanceDepth = 64;
    private const int MaxCollectionItems = 200_000;

    private static readonly uint ClassKey = QbKeyHash.HashLower("Class");
    private static readonly uint TypeKey = QbKeyHash.HashLower("Type");
    private static readonly uint ProfileKey = QbKeyHash.HashLower("profile");
    private static readonly uint NameKey = QbKeyHash.HashLower("name");
    private static readonly uint PositionKey = QbKeyHash.HashLower("pos");
    private static readonly uint AnglesKey = QbKeyHash.HashLower("Angles");
    private static readonly uint GameObjectValue = QbKeyHash.HashLower("gameobject");
    private static readonly uint ModelExportValue = QbKeyHash.HashLower("ModelExport");
    private static readonly uint CreatedAtStartFlag = QbKeyHash.HashLower("CreatedAtStart");
    private static readonly uint RenderToViewportFlag = QbKeyHash.HashLower("RenderToViewport");
    private static readonly uint AbsentInNetGamesFlag = QbKeyHash.HashLower("AbsentInNetGames");

    internal static IReadOnlyDictionary<long, ObjectResourceInstances> Resolve(
        byte[] pakBytes,
        IReadOnlyList<(uint TypeHash, ArchiveEntry Entry)> typedEntries,
        bool isNetworkArchive = false)
    {
        ArgumentNullException.ThrowIfNull(pakBytes);
        ArgumentNullException.ThrowIfNull(typedEntries);

        var triads = FindObjectResourceTriads(typedEntries);
        if (triads.Count == 0)
            return new Dictionary<long, ObjectResourceInstances>();

        // A profile must identify one and only one MQB/MDL owner. CRC collisions or
        // duplicate resource declarations are ambiguous and retain the legacy path.
        var owners = new Dictionary<uint, ObjectResourceTriad>();
        foreach (var group in triads.GroupBy(static triad => triad.ProfileChecksum))
        {
            if (group.Count() != 1)
                continue;

            var triad = group.Single();
            if (!TryReadEntry(pakBytes, triad.OwnerQbEntry, out var ownerBytes))
                continue;

            QbFile ownerQb;
            try
            {
                ownerQb = QbFile.Parse(ownerBytes, triad.OwnerQbEntry.FullName);
            }
            catch (Exception exception) when (IsOptionalQbParseFailure(exception))
            {
                continue;
            }

            if (!HasSingleModelExportNode(ownerQb, triad.OwnerName))
                continue;

            owners.Add(triad.ProfileChecksum, triad);
        }

        if (owners.Count == 0)
            return new Dictionary<long, ObjectResourceInstances>();

        var placementQbs = new List<QbFile>();
        foreach (var (typeHash, entry) in typedEntries)
        {
            if (typeHash != NodeQbTypeHash || !TryReadEntry(pakBytes, entry, out var qbBytes))
                continue;

            try
            {
                placementQbs.Add(QbFile.Parse(qbBytes, entry.FullName));
            }
            catch (Exception exception) when (IsOptionalQbParseFailure(exception))
            {
                // QB metadata is optional for geometry export. A malformed candidate
                // must leave the existing worldzone/object behavior untouched.
            }
        }

        if (placementQbs.Count == 0)
            return new Dictionary<long, ObjectResourceInstances>();

        var result = new Dictionary<long, ObjectResourceInstances>();
        foreach (var ownerGroup in owners.GroupBy(static owner => owner.Value.MdlEntry.Offset))
        {
            // Different profiles claiming the same payload offset are malformed
            // ownership evidence. Leave that MDL on the legacy path.
            if (ownerGroup.Count() != 1)
                continue;

            var (profile, triad) = ownerGroup.Single();
            if (!TryResolveProfileInstances(placementQbs, profile, isNetworkArchive, out var instances))
                continue;

            result.Add(triad.MdlEntry.Offset, new ObjectResourceInstances(
                triad.OwnerName,
                profile,
                triad.MdlEntry,
                instances));
        }

        return result;
    }

    internal static IReadOnlyList<ObjectResourceTriad> FindObjectResourceTriads(
        IReadOnlyList<(uint TypeHash, ArchiveEntry Entry)> typedEntries)
    {
        ArgumentNullException.ThrowIfNull(typedEntries);

        var result = new List<ObjectResourceTriad>();
        for (var i = 0; i + 2 < typedEntries.Count; i++)
        {
            var owner = typedEntries[i];
            var mdl = typedEntries[i + 1];
            var collision = typedEntries[i + 2];
            if (owner.TypeHash != ModelQbTypeHash ||
                mdl.TypeHash != Ps2WorldzoneDetection.WorldzoneMdlTypeHash ||
                collision.TypeHash != CollisionTypeHash ||
                owner.Entry.Crc == 0 ||
                mdl.Entry.Crc != 0 ||
                collision.Entry.Crc != 0 ||
                owner.Entry.Offset < 0 ||
                mdl.Entry.Offset <= owner.Entry.Offset ||
                collision.Entry.Offset <= mdl.Entry.Offset ||
                !TryGetOwnerName(owner.Entry.Name, out var ownerName))
            {
                continue;
            }

            result.Add(new ObjectResourceTriad(
                owner.Entry,
                mdl.Entry,
                collision.Entry,
                ownerName,
                QbKeyHash.HashLower(ownerName)));
        }

        return result;
    }

    internal static bool TryResolveProfileInstances(
        IReadOnlyList<QbFile> qbFiles,
        uint profileChecksum,
        bool isNetworkArchive,
        out IReadOnlyList<QbObjectInstance> instances)
    {
        ArgumentNullException.ThrowIfNull(qbFiles);
        instances = [];

        var matches = new List<NodeArrayMatch>();
        foreach (var qb in qbFiles)
        {
            if (!TryParseGlobals(qb, out var globals))
                continue;

            var resolver = new StructResolver(globals);
            foreach (var (globalChecksum, value) in globals)
            {
                if (value is not ArrayValue array)
                    continue;

                var outcome = TryMatchNodeArray(
                    qb.FileName,
                    globalChecksum,
                    array,
                    profileChecksum,
                    isNetworkArchive,
                    globals,
                    resolver,
                    out var match);
                if (outcome == NodeArrayOutcome.InvalidTarget)
                    return false;
                if (outcome == NodeArrayOutcome.Match)
                    matches.Add(match);
            }
        }

        // Matching the same profile in multiple NodeArrays is not enough evidence
        // to choose one scene. Fail open to the pre-existing origin/root behavior.
        if (matches.Count != 1)
            return false;

        instances = matches[0].Instances;
        return true;
    }

    internal static Quaternion CreateNodeArrayRotation(Vector3 angles)
    {
        if (!IsFinite(angles))
            throw new ArgumentOutOfRangeException(nameof(angles), "QB angles must be finite.");

        // THUG/THAW Mth::Matrix::SetFromAngles: direct radians, Rx * Ry * Rz.
        var matrix = Matrix4x4.CreateRotationX(angles.X) *
                     Matrix4x4.CreateRotationY(angles.Y) *
                     Matrix4x4.CreateRotationZ(angles.Z);
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
    }

    private static NodeArrayOutcome TryMatchNodeArray(
        string qbName,
        uint globalChecksum,
        ArrayValue array,
        uint profileChecksum,
        bool isNetworkArchive,
        IReadOnlyDictionary<uint, IValue> globals,
        StructResolver resolver,
        out NodeArrayMatch match)
    {
        match = null!;
        var matchedInstances = new List<QbObjectInstance>();
        var nodeNames = new HashSet<uint>();
        var sawTargetReference = false;

        foreach (var value in array.Items)
        {
            if (value is not StructValue node)
                continue;

            var targetReferences = new List<uint>();
            foreach (var member in node.Members)
            {
                if (member.BareName is not { } bareName ||
                    !globals.TryGetValue(bareName, out var inheritedValue) ||
                    inheritedValue is not StructValue ||
                    !resolver.TryResolveGlobal(bareName, out var inherited))
                {
                    continue;
                }

                if (IsProfileTemplate(inherited, profileChecksum))
                    targetReferences.Add(bareName);
            }

            if (!resolver.TryResolveInline(node, out var resolved))
            {
                if (targetReferences.Count > 0)
                    return NodeArrayOutcome.InvalidTarget;
                continue;
            }

            if (!IsProfileTemplate(resolved, profileChecksum))
            {
                if (targetReferences.Count > 0)
                    return NodeArrayOutcome.InvalidTarget;
                continue;
            }

            // Most exported nodes inherit a compressed profile struct, while a
            // small proven subset stores the same fields inline. Both are valid;
            // multiple profile bases on one node remain ambiguous.
            sawTargetReference = true;
            if (targetReferences.Count > 1 ||
                !TryGetName(resolved, NameKey, out var nodeName) ||
                !TryGetVector(resolved, PositionKey, out var position) ||
                !TryGetVector(resolved, AnglesKey, out var angles) ||
                !IsFinite(position) ||
                !IsFinite(angles) ||
                !nodeNames.Add(nodeName))
            {
                return NodeArrayOutcome.InvalidTarget;
            }

            if (IsEligibleTemplate(resolved, isNetworkArchive))
            {
                matchedInstances.Add(new QbObjectInstance(
                    nodeName,
                    position,
                    angles,
                    CreateNodeArrayRotation(angles)));
            }
        }

        if (!sawTargetReference)
            return NodeArrayOutcome.NoMatch;

        match = new NodeArrayMatch(qbName, globalChecksum, matchedInstances);
        return NodeArrayOutcome.Match;
    }

    private static bool IsProfileTemplate(ResolvedStruct value, uint profileChecksum)
    {
        return TryGetName(value, ClassKey, out var classValue) && classValue == GameObjectValue &&
               TryGetName(value, TypeKey, out var typeValue) && typeValue == profileChecksum &&
               TryGetName(value, ProfileKey, out var profileValue) && profileValue == profileChecksum;
    }

    private static bool IsEligibleTemplate(ResolvedStruct value, bool isNetworkArchive)
    {
        return value.Flags.Contains(CreatedAtStartFlag) &&
               value.Flags.Contains(RenderToViewportFlag) &&
               (!isNetworkArchive || !value.Flags.Contains(AbsentInNetGamesFlag));
    }

    internal static bool HasSingleModelExportNode(QbFile qb, string ownerName)
    {
        if (!TryParseGlobals(qb, out var globals))
            return false;

        var expectedNodeArray = QbKeyHash.HashLower(ownerName + "_NodeArray");
        if (!globals.TryGetValue(expectedNodeArray, out var nodeArrayValue) ||
            nodeArrayValue is not ArrayValue nodeArray)
        {
            return false;
        }

        var resolver = new StructResolver(globals);
        var count = 0;
        foreach (var item in nodeArray.Items)
        {
            if (item is not StructValue node)
                return false;

            if (!resolver.TryResolveInline(node, out var resolved) ||
                !TryGetName(resolved, ClassKey, out var classValue))
                return false;
            if (classValue != ModelExportValue)
                continue;
            if (!TryGetName(resolved, NameKey, out _))
                return false;

            count++;
            if (count > 1)
                return false;
        }

        return count == 1;
    }

    private static bool TryParseGlobals(QbFile qb, out IReadOnlyDictionary<uint, IValue> globals)
    {
        var parsed = new Dictionary<uint, IValue>();
        var ambiguous = new HashSet<uint>();

        foreach (var item in qb.Items)
        {
            if (item.Kind != QbItemKind.Global ||
                item.StartTokenIndex < 0 ||
                item.EndTokenIndex <= item.StartTokenIndex ||
                item.EndTokenIndex > qb.Tokens.Count ||
                item.StartTokenIndex + 2 >= item.EndTokenIndex)
            {
                continue;
            }

            var start = item.StartTokenIndex;
            if (qb.Tokens[start].Type != QbTokenType.Name ||
                qb.Tokens[start + 1].Type != QbTokenType.Equals)
            {
                continue;
            }

            var index = start + 2;
            if (!TryReadValue(qb.Tokens, ref index, item.EndTokenIndex, 0, out var value))
                continue;

            while (index < item.EndTokenIndex && qb.Tokens[index].Type is
                       QbTokenType.EndOfLine or QbTokenType.EndOfLineNumber)
            {
                index++;
            }

            if (index != item.EndTokenIndex)
                continue;

            if (!parsed.TryAdd(item.NameChecksum, value))
                ambiguous.Add(item.NameChecksum);
        }

        foreach (var checksum in ambiguous)
            parsed.Remove(checksum);

        globals = parsed;
        return parsed.Count > 0;
    }

    private static bool TryReadValue(
        IReadOnlyList<QbToken> tokens,
        ref int index,
        int end,
        int depth,
        out IValue value)
    {
        value = null!;
        if (depth > MaxValueDepth || index < 0 || index >= end || index >= tokens.Count)
            return false;

        var token = tokens[index];
        switch (token.Type)
        {
            case QbTokenType.Name:
            case QbTokenType.Enum:
                value = new NameValue(token.NameChecksum);
                index++;
                return true;

            case QbTokenType.Vector:
                value = new VectorValue(new Vector3(token.FloatX, token.FloatY, token.FloatZ));
                index++;
                return true;

            case QbTokenType.Pair:
                value = new PairValue(new Vector2(token.FloatX, token.FloatY));
                index++;
                return true;

            case QbTokenType.Integer:
            case QbTokenType.EndOfLineNumber:
                value = new IntegerValue(token.IntValue);
                index++;
                return true;

            case QbTokenType.HexInteger:
                value = new IntegerValue(unchecked((int)token.HexValue));
                index++;
                return true;

            case QbTokenType.Float:
                value = new FloatValue(token.FloatValue);
                index++;
                return true;

            case QbTokenType.String:
            case QbTokenType.LocalString:
                value = new StringValue(token.StringValue ?? string.Empty);
                index++;
                return true;

            case QbTokenType.StartArray:
            {
                index++;
                var items = new List<IValue>();
                while (index < end && tokens[index].Type != QbTokenType.EndArray)
                {
                    if (items.Count >= MaxCollectionItems ||
                        !TryReadValue(tokens, ref index, end, depth + 1, out var child))
                    {
                        return false;
                    }

                    items.Add(child);
                }

                if (index >= end || tokens[index].Type != QbTokenType.EndArray)
                    return false;
                index++;
                value = new ArrayValue(items);
                return true;
            }

            case QbTokenType.StartStruct:
            {
                index++;
                var members = new List<StructMember>();
                while (index < end && tokens[index].Type != QbTokenType.EndStruct)
                {
                    if (members.Count >= MaxCollectionItems)
                        return false;

                    if (tokens[index].Type == QbTokenType.Name)
                    {
                        var key = tokens[index].NameChecksum;
                        if (index + 1 < end && tokens[index + 1].Type == QbTokenType.Equals)
                        {
                            index += 2;
                            if (!TryReadValue(tokens, ref index, end, depth + 1, out var fieldValue))
                                return false;
                            members.Add(new StructMember(key, fieldValue, null));
                        }
                        else
                        {
                            members.Add(new StructMember(null, null, key));
                            index++;
                        }

                        continue;
                    }

                    // Unkeyed values occur in some structs but have no bearing on
                    // NodeArray inheritance. Parse them to preserve synchronization.
                    if (!TryReadValue(tokens, ref index, end, depth + 1, out _))
                        return false;
                }

                if (index >= end || tokens[index].Type != QbTokenType.EndStruct)
                    return false;
                index++;
                value = new StructValue(members);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool TryGetName(ResolvedStruct value, uint key, out uint checksum)
    {
        checksum = 0;
        if (!value.Fields.TryGetValue(key, out var field) || field is not NameValue name)
            return false;
        checksum = name.Checksum;
        return true;
    }

    private static bool TryGetVector(ResolvedStruct value, uint key, out Vector3 vector)
    {
        vector = default;
        if (!value.Fields.TryGetValue(key, out var field) || field is not VectorValue item)
            return false;
        vector = item.Vector;
        return true;
    }

    private static bool TryReadEntry(byte[] pakBytes, ArchiveEntry entry, out byte[] data)
    {
        data = [];
        if (entry.InCompanion || entry.Offset < 0 || entry.Size <= 0 ||
            entry.Offset > int.MaxValue || entry.Size > int.MaxValue ||
            entry.Offset > pakBytes.Length - entry.Size)
        {
            return false;
        }

        data = pakBytes.AsSpan(checked((int)entry.Offset), checked((int)entry.Size)).ToArray();
        return true;
    }

    private static bool TryGetOwnerName(string entryName, out string ownerName)
    {
        ownerName = string.Empty;
        if (string.IsNullOrWhiteSpace(entryName))
            return false;

        var fileName = entryName.Replace('\\', '/').Split('/')[^1];
        foreach (var suffix in new[] { ".mqb.ps2", ".qb.ps2", ".mqb", ".qb" })
        {
            if (!fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            ownerName = fileName[..^suffix.Length];
            return ownerName.Length > 0;
        }

        return false;
    }

    private static bool IsOptionalQbParseFailure(Exception exception)
    {
        return exception is IOException or ArgumentException or
            IndexOutOfRangeException or OverflowException;
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    }

    internal sealed record ObjectResourceInstances(
        string OwnerName,
        uint ProfileChecksum,
        ArchiveEntry MdlEntry,
        IReadOnlyList<QbObjectInstance> Instances);

    internal readonly record struct QbObjectInstance(
        uint NodeChecksum,
        Vector3 Position,
        Vector3 Angles,
        Quaternion Rotation);

    internal readonly record struct ObjectResourceTriad(
        ArchiveEntry OwnerQbEntry,
        ArchiveEntry MdlEntry,
        ArchiveEntry CollisionEntry,
        string OwnerName,
        uint ProfileChecksum);

    private sealed record NodeArrayMatch(
        string QbName,
        uint GlobalChecksum,
        IReadOnlyList<QbObjectInstance> Instances);

    private enum NodeArrayOutcome
    {
        NoMatch,
        Match,
        InvalidTarget
    }

    private interface IValue;
    private sealed record NameValue(uint Checksum) : IValue;
    private sealed record VectorValue(Vector3 Vector) : IValue;
    private sealed record PairValue(Vector2 Pair) : IValue;
    private sealed record IntegerValue(int Integer) : IValue;
    private sealed record FloatValue(float Float) : IValue;
    private sealed record StringValue(string String) : IValue;
    private sealed record ArrayValue(IReadOnlyList<IValue> Items) : IValue;
    private sealed record StructValue(IReadOnlyList<StructMember> Members) : IValue;
    private readonly record struct StructMember(uint? Key, IValue? Value, uint? BareName);

    private sealed record ResolvedStruct(
        IReadOnlyDictionary<uint, IValue> Fields,
        IReadOnlySet<uint> Flags);

    private sealed class StructResolver(IReadOnlyDictionary<uint, IValue> globals)
    {
        private readonly Dictionary<uint, ResolvedStruct?> _cache = new();

        public bool TryResolveGlobal(uint checksum, out ResolvedStruct resolved)
        {
            return TryResolveGlobal(checksum, new HashSet<uint>(), out resolved);
        }

        public bool TryResolveInline(StructValue value, out ResolvedStruct resolved)
        {
            return TryResolve(value, new HashSet<uint>(), out resolved);
        }

        private bool TryResolveGlobal(
            uint checksum,
            HashSet<uint> stack,
            out ResolvedStruct resolved)
        {
            resolved = null!;
            if (_cache.TryGetValue(checksum, out var cached))
            {
                if (cached == null)
                    return false;
                resolved = cached;
                return true;
            }

            if (stack.Count >= MaxInheritanceDepth ||
                !stack.Add(checksum) ||
                !globals.TryGetValue(checksum, out var value) ||
                value is not StructValue structure ||
                !TryResolve(structure, stack, out resolved))
            {
                _cache[checksum] = null;
                return false;
            }

            stack.Remove(checksum);
            _cache[checksum] = resolved;
            return true;
        }

        private bool TryResolve(
            StructValue value,
            HashSet<uint> stack,
            out ResolvedStruct resolved)
        {
            var fields = new Dictionary<uint, IValue>();
            var flags = new HashSet<uint>();

            // Template references are independent of member order. Merge every
            // inherited struct first, rejecting conflicting bases, so explicit
            // node-local fields always win even when they precede the template in
            // the token stream.
            var localFields = new Dictionary<uint, IValue>();
            foreach (var member in value.Members)
            {
                if (member.BareName is not { } bareName ||
                    !globals.TryGetValue(bareName, out var inheritedValue) ||
                    inheritedValue is not StructValue)
                    continue;

                if (!TryResolveGlobal(bareName, stack, out var inherited))
                {
                    resolved = null!;
                    return false;
                }

                foreach (var (inheritedKey, inheritedField) in inherited.Fields)
                {
                    if (fields.TryGetValue(inheritedKey, out var existing) &&
                        existing != inheritedField)
                    {
                        resolved = null!;
                        return false;
                    }

                    fields[inheritedKey] = inheritedField;
                }

                flags.UnionWith(inherited.Flags);
            }

            foreach (var member in value.Members)
            {
                if (member.BareName is { } bareName &&
                    (!globals.TryGetValue(bareName, out var inheritedValue) ||
                     inheritedValue is not StructValue))
                {
                    flags.Add(bareName);
                    continue;
                }

                if (member.Key is not { } key || member.Value == null)
                    continue;

                if (localFields.TryGetValue(key, out var existing) && existing != member.Value)
                {
                    resolved = null!;
                    return false;
                }

                localFields[key] = member.Value;
            }

            foreach (var (key, localValue) in localFields)
                fields[key] = localValue;

            resolved = new ResolvedStruct(fields, flags);
            return true;
        }
    }
}
