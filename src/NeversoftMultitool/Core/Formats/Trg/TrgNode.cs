using System.Text;
using static NeversoftMultitool.Core.Formats.Trg.TrgNodeMetadata;

namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
///     Represents a parsed node from a TRG file. Node type determines which fields are populated.
/// </summary>
public sealed class TrgNode
{
    // --- Common fields (always present) ---
    public int Index { get; init; }
    public int TypeId { get; init; }
    public string Type { get; init; } = "UNKNOWN";
    public uint Offset { get; init; }

    // --- Optional fields (type-dependent) ---
    public List<int>? Links { get; set; }
    public TrgPosition? Position { get; set; }
    public TrgAngles? Angles { get; set; }
    public string? Name { get; set; }
    public uint? Checksum { get; set; }
    public int? SubType { get; set; }
    public string? SubTypeName { get; set; }
    public List<byte>? BaddyFlags { get; set; }

    /// <summary>Primary model placed by a MANIPOB (subtype 401) entity node.</summary>
    public uint? ModelChecksum { get; set; }

    /// <summary>
    ///     Existing environment item a MANIPOB is associated with (looked up
    ///     via Spool_FindEnviroItem, not instanced by the node).
    /// </summary>
    public uint? AssocModelChecksum { get; set; }

    /// <summary>
    ///     Alternate/damage-state models swapped in by events. Not placed at
    ///     level start.
    /// </summary>
    public List<uint>? AlternateModelChecksums { get; set; }

    public int? CameraRadius { get; set; }
    public int? CameraMode { get; set; }
    public string? CameraModeName { get; set; }
    public int? PickupType { get; set; }
    public string? PickupTypeName { get; set; }

    /// <summary>
    ///     POWERUP grounded check (0 = grounded → the engine snaps the pickup to
    ///     the terrain via <c>Utils_GetGroundHeight</c>; see <c>CPowerUp</c> ctor
    ///     flag bit 2). Null for non-POWERUP nodes.
    /// </summary>
    public ushort? GroundedCheck { get; set; }
    public int? TerrainType { get; set; }
    public string? TerrainTypeName { get; set; }
    public TrgLightParams? LightParams { get; set; }
    public List<TrgCommand>? Commands { get; set; }
    public List<TrgScriptOp>? Script { get; set; }
    public string? RawHex { get; set; }

    public static TrgNode Parse(BinaryReader reader, int index, uint offset, int nodeSize, bool isSpiderMan)
    {
        var startPos = reader.BaseStream.Position;
        var typeId = reader.ReadUInt16();
        var typeName = NodeTypeNames.GetValueOrDefault(typeId, $"UNKNOWN_{typeId}");

        var node = new TrgNode
        {
            Index = index,
            TypeId = typeId,
            Type = typeName,
            Offset = offset
        };

        try
        {
            switch (typeId)
            {
                case TypeTerminator:
                    break;

                case TypeRestart:
                    ParseRestart(reader, node, startPos, nodeSize);
                    break;

                case TypePoint:
                    ParsePoint(reader, node);
                    break;

                case TypeRailDef:
                case TypeRailPoint:
                    ParseRailPoint(reader, node, isSpiderMan);
                    break;

                case TypeCamPt:
                    ParseCamPt(reader, node);
                    break;

                case TypeBaddy:
                case 7: // SEEDABLEBADDY — same entity system as BADDY
                    ParseBaddy(reader, node, startPos, nodeSize);
                    break;

                case TypeTrickOb:
                case TypeGoalOb:
                    ParseTrickOrGoalOb(reader, node, isSpiderMan);
                    break;

                case TypeCrate:
                    ParseCrate(reader, node);
                    break;

                case TypePowerup:
                    ParsePowerup(reader, node, isSpiderMan);
                    break;

                case TypeCommandPoint:
                    ParseCommandPoint(reader, node, startPos, nodeSize);
                    break;

                case TypeAutoexec:
                case TypeAutoexec2:
                    ParseAutoexec(reader, node, startPos, nodeSize);
                    break;

                case TypeLight:
                case TypeOffLight:
                    ParseLight(reader, node, startPos, nodeSize);
                    break;

                case TypeScriptPoint:
                case TypeCameraPath:
                    ParseScriptPoint(reader, node, startPos, nodeSize);
                    break;

                case TypeEnhancedSpawn:
                    ParseEnhancedSpawn(reader, node);
                    break;

                default:
                    // Store raw hex for unknown types
                    reader.BaseStream.Position = startPos;
                    var rawBytes = reader.ReadBytes(nodeSize);
                    node.RawHex = Convert.ToHexString(rawBytes);
                    break;
            }
        }
        catch
        {
            // On parse error, store what we can as raw hex
            reader.BaseStream.Position = startPos;
            var rawBytes = reader.ReadBytes(nodeSize);
            node.RawHex = Convert.ToHexString(rawBytes);
        }

        return node;
    }

    private static List<int> ReadLinks(BinaryReader reader, bool align = true)
    {
        var count = reader.ReadUInt16();
        var links = new List<int>(count);
        for (var i = 0; i < count; i++)
            links.Add(reader.ReadUInt16());

        if (align)
            AlignTo4(reader);
        return links;
    }

    private static void AlignTo4(BinaryReader reader)
    {
        var pos = reader.BaseStream.Position;
        if (pos % 4 != 0)
            reader.BaseStream.Position = pos + (4 - pos % 4);
    }

    private static TrgPosition ReadPosition(BinaryReader reader)
    {
        // TRG positions are serialized as whole engine/model units. Runtime
        // SquirtPos/Trig_GetPosition shifts each component left by 12 when it
        // copies it into a 20.12 CVector. Keep the legacy decoded properties
        // stable for JSON consumers, but retain the exact integers for code
        // which needs to reproduce runtime placement.
        var rawX = reader.ReadInt32();
        var rawY = reader.ReadInt32();
        var rawZ = reader.ReadInt32();
        return new TrgPosition
        {
            RawX = rawX,
            RawY = rawY,
            RawZ = rawZ,
            X = rawX / 4096.0,
            Y = rawY / 4096.0,
            Z = rawZ / 4096.0
        };
    }

    private static TrgAngles ReadAngles(BinaryReader reader)
    {
        // Angles are 16-bit values where 4096 = full circle (360 degrees)
        var rawX = reader.ReadInt16();
        var rawY = reader.ReadInt16();
        var rawZ = reader.ReadInt16();
        return new TrgAngles
        {
            RawX = rawX,
            RawY = rawY,
            RawZ = rawZ,
            X = Math.Round(rawX * 360.0 / 4096.0, 2),
            Y = Math.Round(rawY * 360.0 / 4096.0, 2),
            Z = Math.Round(rawZ * 360.0 / 4096.0, 2)
        };
    }

    private static string ReadNullTerminatedString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            var b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }

        // Align to 2-byte boundary after null terminator
        var pos = reader.BaseStream.Position;
        if (pos % 2 != 0)
            reader.BaseStream.Position = pos + 1;

        return sb.ToString();
    }

    // --- Node type parsers ---

    private static void ParseRestart(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        node.Links = ReadLinks(reader);
        node.Position = ReadPosition(reader);
        node.Angles = ReadAngles(reader);
        node.Name = ReadNullTerminatedString(reader);

        // Restart nodes have a command list after the name
        var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
        if (remaining > 2)
            node.Commands = TrgCommandList.ParseCommandList(reader, remaining);
    }

    private static void ParsePoint(BinaryReader reader, TrgNode node)
    {
        node.Links = ReadLinks(reader);
        node.Position = ReadPosition(reader);
    }

    private static void ParseRailPoint(BinaryReader reader, TrgNode node, bool isSpiderMan)
    {
        node.Links = ReadLinks(reader);
        node.Position = ReadPosition(reader);

        var terrainType = reader.ReadUInt16();
        node.TerrainType = terrainType;
        node.TerrainTypeName = TerrainTypeNames.GetValueOrDefault(terrainType);

        if (!isSpiderMan)
        {
            // v2.0 (THPS/Apocalypse): extra 6 bytes + 0xFFFF terminator
            // Skip over them
            reader.ReadBytes(6);
            reader.ReadUInt16(); // 0xFFFF
        }
    }

    private static void ParseCamPt(BinaryReader reader, TrgNode node)
    {
        var camLink = reader.ReadUInt16();
        AlignTo4(reader);
        node.Position = ReadPosition(reader);
        node.CameraRadius = reader.ReadUInt16();
        var camType = reader.ReadUInt16();
        node.CameraMode = camType;
        node.CameraModeName = CameraModeNames.GetValueOrDefault(camType);
        node.Links = [camLink];
    }

    private static void ParseBaddy(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        var baddyType = reader.ReadUInt16();
        node.SubType = baddyType;
        node.SubTypeName = BaddyTypeNames.GetValueOrDefault(baddyType);

        var priority = reader.ReadUInt16();

        if (priority is 0x1000 or 0x1001)
        {
            // Runtime type-1/type-7 parsing always reads a byte flag list
            // immediately after the links, regardless of priority, and only
            // aligns after the 0xFF terminator. Flag 1 creates type-1 baddies
            // during the level-start scan; flag 2 places a created baddy on
            // the active list; without it Trig_CreateObject
            // immediately moves the instance to SuspendedList. Flag 4 clears
            // distance-based autosuspension; it is not a visibility bit.
            node.Links = ReadLinks(reader, false);
            node.BaddyFlags = ReadBaddyFlags(reader, startPos + nodeSize);
            AlignTo4(reader);
            node.Position = ReadPosition(reader);
            node.Angles = ReadAngles(reader);

            if (baddyType == BaddyTypeManipOb)
            {
                // MANIPOB payloads are a binary record, not bytecode
                // (CManipOb ctor, spidey-decomp manipob.cpp): aligned u32
                // list [primary, assoc?, alternates…, 0] then three u16s.
                // The assoc entry only exists when nonzero — a zero in
                // slot 1 is the list terminator.
                ParseManipObRecord(reader, node, startPos + nodeSize);
            }
            else
            {
                var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
                if (remaining > 2)
                    node.Script = TrgCommandList.ParseScript(reader, remaining);
            }
        }
        else
        {
            // Unknown priority — try parsing as links + position
            node.Links = ReadLinks(reader);
            var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
            if (remaining >= 12)
                node.Position = ReadPosition(reader);
        }
    }

    private static void ParseManipObRecord(BinaryReader reader, TrgNode node, long endPosition)
    {
        AlignTo4(reader);
        if (reader.BaseStream.Position + 4 > endPosition)
            return;

        node.ModelChecksum = reader.ReadUInt32();

        if (reader.BaseStream.Position + 4 > endPosition)
            return;
        var assoc = reader.ReadUInt32();
        if (assoc == 0)
            return;

        node.AssocModelChecksum = assoc;
        var alternates = new List<uint>();
        while (reader.BaseStream.Position + 4 <= endPosition)
        {
            var alternate = reader.ReadUInt32();
            if (alternate == 0)
                break;
            alternates.Add(alternate);
        }

        if (alternates.Count > 0)
            node.AlternateModelChecksums = alternates;
    }

    private static List<byte> ReadBaddyFlags(BinaryReader reader, long endPosition)
    {
        var flags = new List<byte>();
        while (reader.BaseStream.Position < endPosition)
        {
            var flag = reader.ReadByte();
            if (flag == byte.MaxValue)
                break;
            flags.Add(flag);
        }

        return flags;
    }

    private static void ParseTrickOrGoalOb(BinaryReader reader, TrgNode node, bool isSpiderMan)
    {
        node.Links = ReadLinks(reader);
        node.Checksum = reader.ReadUInt32();

        // v2.0: may have 0xFFFF terminator
        if (!isSpiderMan && reader.BaseStream.Position + 2 <= reader.BaseStream.Length)
        {
            var term = reader.ReadUInt16();
            if (term != 0xFFFF)
                reader.BaseStream.Position -= 2;
        }
    }

    private static void ParseCrate(BinaryReader reader, TrgNode node)
    {
        node.Links = ReadLinks(reader);
        node.Checksum = reader.ReadUInt32();
    }

    private static void ParsePowerup(BinaryReader reader, TrgNode node, bool isSpiderMan)
    {
        // Ghidra: PowerUp_Create(pickupType, position, flags, respawnFlag, velocity)
        // flags bit 2 = grounded (if groundedCheck == 0, use Utils_GetGroundHeight)
        var pickupType = reader.ReadUInt16();
        node.PickupType = pickupType;
        node.PickupTypeName = isSpiderMan ? null : PickupTypeNames.GetValueOrDefault(pickupType);

        // The engine's Trig_GetPosition skips this node's link list before
        // reading the position. Earlier code consumed only the link COUNT, so
        // any POWERUP with links (e.g. the l1a1 "?" markers, 4-5 links each)
        // parsed its links as the position and produced garbage world coords.
        node.Links = ReadLinks(reader);
        node.Position = ReadPosition(reader);
        node.GroundedCheck = reader.ReadUInt16(); // 0 = grounded (snap to terrain)
        reader.ReadUInt16(); // respawn flag: 0 = one-time, 1 = respawning
        reader.ReadUInt16(); // 0xFFFF terminator

        if (!isSpiderMan)
        {
            // v2.0 (THPS): extra 0xFFFF terminator
            reader.ReadUInt16();
        }
    }

    private static void ParseCommandPoint(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        node.Links = ReadLinks(reader);
        node.Checksum = reader.ReadUInt32();

        var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
        if (remaining > 2)
            node.Commands = TrgCommandList.ParseCommandList(reader, remaining);
    }

    private static void ParseAutoexec(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
        if (remaining > 2)
            node.Commands = TrgCommandList.ParseCommandList(reader, remaining);
    }

    private static void ParseLight(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        // Ghidra decompilation of CLight constructor:
        //   CLight(CVector& pos, int nodeIndex, int range, int innerAngle, int outerAngle,
        //          int falloff, Uc r1, Uc g1, Uc b1, Uc r2, Uc g2, Uc b2)
        // Node index is passed externally; data has 4 × int16 + 6 × uint8 = 14 bytes
        AlignTo4(reader);
        var endPos = startPos + nodeSize;

        if (reader.BaseStream.Position + 12 <= endPos)
            node.Position = ReadPosition(reader);

        var remaining = (int)(endPos - reader.BaseStream.Position);
        if (remaining >= 14) // 4×2 + 6×1 = 14 bytes
        {
            var range = reader.ReadInt16();
            var innerAngle = reader.ReadInt16();
            var outerAngle = reader.ReadInt16();
            var falloff = reader.ReadInt16();
            var r1 = reader.ReadByte();
            var g1 = reader.ReadByte();
            var b1 = reader.ReadByte();
            var r2 = reader.ReadByte();
            var g2 = reader.ReadByte();
            var b2 = reader.ReadByte();
            node.LightParams = new TrgLightParams
            {
                Range = range, InnerAngle = innerAngle,
                OuterAngle = outerAngle, Falloff = falloff,
                Color1R = r1, Color1G = g1, Color1B = b1,
                Color2R = r2, Color2G = g2, Color2B = b2
            };
        }
    }

    private static void ParseScriptPoint(BinaryReader reader, TrgNode node, long startPos, int nodeSize)
    {
        // Script points have links + position + angles before the bytecode
        node.Links = ReadLinks(reader);
        if (reader.BaseStream.Position + 12 <= startPos + nodeSize)
            node.Position = ReadPosition(reader);
        if (reader.BaseStream.Position + 6 <= startPos + nodeSize)
            node.Angles = ReadAngles(reader);

        var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
        if (remaining > 2)
            node.Script = TrgCommandList.ParseScript(reader, remaining);
    }

    private static void ParseEnhancedSpawn(BinaryReader reader, TrgNode node)
    {
        reader.ReadUInt16(); // flags
        node.Links = ReadLinks(reader);
        node.Position = ReadPosition(reader);
    }
}
