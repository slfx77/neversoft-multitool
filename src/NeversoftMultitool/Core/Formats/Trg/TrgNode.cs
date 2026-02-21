using System.Text;
using System.Text.Json.Serialization;

namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
/// Represents a parsed node from a TRG file. Node type determines which fields are populated.
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
    public int? CameraRadius { get; set; }
    public int? CameraMode { get; set; }
    public string? CameraModeName { get; set; }
    public int? PickupType { get; set; }
    public string? PickupTypeName { get; set; }
    public int? TerrainType { get; set; }
    public string? TerrainTypeName { get; set; }
    public TrgLightParams? LightParams { get; set; }
    public List<TrgCommand>? Commands { get; set; }
    public List<TrgScriptOp>? Script { get; set; }
    public string? RawHex { get; set; }

    // Node type IDs
    private const int TypeBaddy = 1;
    private const int TypeCrate = 2;
    private const int TypePoint = 3;
    private const int TypeAutoexec = 4;
    private const int TypePowerup = 5;
    private const int TypeCommandPoint = 6;
    private const int TypeRestart = 8;
    private const int TypeBarrel = 9;
    private const int TypeRailDef = 10;
    private const int TypeRailPoint = 11;
    private const int TypeTrickOb = 12;
    private const int TypeCamPt = 13;
    private const int TypeGoalOb = 14;
    private const int TypeAutoexec2 = 15;
    private const int TypeMyst = 16;
    private const int TypeTerminator = 255;
    private const int TypeLight = 500;
    private const int TypeOffLight = 501;
    private const int TypeScriptPoint = 1000;
    private const int TypeCameraPath = 1001;
    private const int TypeEnhancedSpawn = 1002;

    private static readonly Dictionary<int, string> NodeTypeNames = new()
    {
        [TypeBaddy] = "BADDY",
        [TypeCrate] = "CRATE",
        [TypePoint] = "POINT",
        [TypeAutoexec] = "AUTOEXEC",
        [TypePowerup] = "POWERUP",
        [TypeCommandPoint] = "COMMANDPOINT",
        [7] = "SEEDABLEBADDY",
        [TypeRestart] = "RESTART",
        [TypeBarrel] = "BARREL",
        [TypeRailDef] = "RAILDEF",
        [TypeRailPoint] = "RAILPOINT",
        [TypeTrickOb] = "TRICKOB",
        [TypeCamPt] = "CAMPT",
        [TypeGoalOb] = "GOALOB",
        [TypeAutoexec2] = "AUTOEXEC2",
        [TypeMyst] = "MYST",
        [TypeTerminator] = "TERMINATOR",
        [TypeLight] = "LIGHT",
        [TypeOffLight] = "OFFLIGHT",
        [TypeScriptPoint] = "SCRIPTPOINT",
        [TypeCameraPath] = "CAMERAPATH",
        [TypeEnhancedSpawn] = "ENHANCEDSPAWN"
    };

    private static readonly Dictionary<int, string> BaddyTypeNames = new()
    {
        [203] = "SCRIPTONLYBADDY",
        [303] = "MJ",
        [304] = "THUG",
        [306] = "POLICE",
        [307] = "RHINO",
        [308] = "DOCOCK",
        [309] = "SUPERDOCOCK",
        [310] = "SCORPION",
        [311] = "MYSTERIO",
        [312] = "HENCHMAN",
        [313] = "VENOM",
        [314] = "CARNAGE",
        [315] = "HOSTAGE",
        [316] = "JONAH",
        [317] = "LIZMAN",
        [318] = "BADDYCHOPPER",
        [319] = "BLACKCAT",
        [320] = "SWAT",
        [402] = "PLATFORM",
        [403] = "LEVER",
        [404] = "LASERFENCE",
        [405] = "TRIPWIRE",
        [407] = "SWITCH",
        [408] = "LASERBEAM",
        [409] = "ELECTROLINE",
        [411] = "SIMBYDROPLET",
        [412] = "PUNCHOB"
    };

    private static readonly Dictionary<int, string> PickupTypeNames = new()
    {
        [4] = "KPickup",
        [5] = "SPickup",
        [6] = "APickup",
        [10] = "EPickup",
        [15] = "TPickup",
        [16] = "TapePickup",
        [21] = "BonusPickup100",
        [22] = "BonusPickup200",
        [23] = "BonusPickup500",
        [24] = "MoneyPickup250",
        [25] = "MoneyPickup50",
        [26] = "MoneyPickup100",
        [33] = "LevelPickup"
    };

    // ECameraMode enum from THPS2 MAIN.SYM debug symbols
    private static readonly Dictionary<int, string> CameraModeNames = new()
    {
        [0] = "Nothing",
        [1] = "Normal",
        [2] = "NoBigAir",
        [3] = "Demo",
        [4] = "Start",
        [5] = "Far",
        [6] = "Overhead",
        [7] = "Front",
        [8] = "Idle",
        [9] = "Flying",
        [10] = "FunkyFlying",
        [11] = "RollerCoaster",
        [12] = "Pan",
        [13] = "ItsyLookDown",
        [14] = "ItsyLookUp",
        [15] = "Loose",
        [16] = "User",
        [17] = "LookAround",
        [18] = "UpsideTest",
        [19] = "BossBeast",
        [20] = "BossWar",
        [21] = "BossTank",
        [22] = "Debug",
        [23] = "CompetitionIntro"
    };

    private static readonly Dictionary<int, string> TerrainTypeNames = new()
    {
        [0] = "Concrete",
        [1] = "Metal",
        [2] = "Wood"
    };

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
                    ParseRestart(reader, node, startPos, nodeSize, isSpiderMan);
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
                    ParseBaddy(reader, node, startPos, nodeSize, isSpiderMan);
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

    private static List<int> ReadLinks(BinaryReader reader)
    {
        var count = reader.ReadUInt16();
        var links = new List<int>(count);
        for (var i = 0; i < count; i++)
            links.Add(reader.ReadUInt16());

        // Align to 4-byte boundary
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
        return new TrgPosition
        {
            X = reader.ReadInt32() / 4096.0,
            Y = reader.ReadInt32() / 4096.0,
            Z = reader.ReadInt32() / 4096.0
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

    private static void ParseRestart(BinaryReader reader, TrgNode node, long startPos, int nodeSize, bool isSpiderMan)
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

    private static void ParseBaddy(BinaryReader reader, TrgNode node, long startPos, int nodeSize, bool isSpiderMan)
    {
        var baddyType = reader.ReadUInt16();
        node.SubType = baddyType;
        node.SubTypeName = BaddyTypeNames.GetValueOrDefault(baddyType);

        var priority = reader.ReadUInt16();

        if (priority == 0x1001)
        {
            // Simple placement
            node.Links = ReadLinks(reader);
            node.Position = ReadPosition(reader);
            node.Angles = ReadAngles(reader);

            var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
            if (remaining > 2)
                node.Script = TrgCommandList.ParseScript(reader, remaining);
        }
        else if (priority == 0x1000)
        {
            // Full baddy with script
            // Ghidra: flags are a byte array terminated by 0xFF:
            //   flag 2 = initially active, flag 4 = initially invisible
            //   v4/v5 are packed flag bytes (e.g., 0x0200 = [0x00, 0x02], 0xFF04 = [0x04, 0xFF])
            node.Links = ReadLinks(reader);
            reader.ReadUInt16(); // flag bytes (packed): active/visible state
            reader.ReadUInt16(); // flag bytes (packed): 0xFF terminator + next byte
            AlignTo4(reader);
            node.Position = ReadPosition(reader);
            node.Angles = ReadAngles(reader);

            var remaining = (int)(startPos + nodeSize - reader.BaseStream.Position);
            if (remaining > 2)
                node.Script = TrgCommandList.ParseScript(reader, remaining);
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

    private static void ParseTrickOrGoalOb(BinaryReader reader, TrgNode node, bool isSpiderMan)
    {
        node.Links = ReadLinks(reader);
        node.Checksum = reader.ReadUInt32();

        if (!isSpiderMan)
        {
            // v2.0: may have 0xFFFF terminator
            if (reader.BaseStream.Position + 2 <= reader.BaseStream.Length)
            {
                var term = reader.ReadUInt16();
                if (term != 0xFFFF)
                    reader.BaseStream.Position -= 2;
            }
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

        reader.ReadUInt16(); // link count (used by Trig_GetPosition to skip links)
        AlignTo4(reader);
        node.Position = ReadPosition(reader);
        reader.ReadUInt16(); // grounded check: 0 = grounded (snap to terrain)
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

public sealed class TrgPosition
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

public sealed class TrgAngles
{
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
}

/// <summary>
/// Light parameters from CLight constructor (Ghidra decompilation):
/// CLight(CVector&amp; pos, int nodeIndex, int range, int innerAngle, int outerAngle,
///        int falloff, Uc r1, Uc g1, Uc b1, Uc r2, Uc g2, Uc b2)
/// Node index passed externally; data contains 4 × int16 + 6 × uint8 = 14 bytes.
/// </summary>
public sealed class TrgLightParams
{
    public int Range { get; init; }
    public int InnerAngle { get; init; }
    public int OuterAngle { get; init; }
    public int Falloff { get; init; }
    public byte Color1R { get; init; }
    public byte Color1G { get; init; }
    public byte Color1B { get; init; }
    public byte Color2R { get; init; }
    public byte Color2G { get; init; }
    public byte Color2B { get; init; }
}

