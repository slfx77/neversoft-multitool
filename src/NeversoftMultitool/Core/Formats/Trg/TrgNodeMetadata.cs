namespace NeversoftMultitool.Core.Formats.Trg;

internal static class TrgNodeMetadata
{
    internal const int TypeBaddy = 1;
    internal const int TypeCrate = 2;
    internal const int TypePoint = 3;
    internal const int TypeAutoexec = 4;
    internal const int TypePowerup = 5;
    internal const int TypeCommandPoint = 6;
    internal const int TypeRestart = 8;
    internal const int TypeBarrel = 9;
    internal const int TypeRailDef = 10;
    internal const int TypeRailPoint = 11;
    internal const int TypeTrickOb = 12;
    internal const int TypeCamPt = 13;
    internal const int TypeGoalOb = 14;
    internal const int TypeAutoexec2 = 15;
    internal const int TypeMyst = 16;
    internal const int TypeTerminator = 255;
    internal const int TypeLight = 500;
    internal const int TypeOffLight = 501;
    internal const int TypeScriptPoint = 1000;
    internal const int TypeCameraPath = 1001;
    internal const int TypeEnhancedSpawn = 1002;

    internal static readonly Dictionary<int, string> NodeTypeNames = new()
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

    internal static readonly Dictionary<int, string> BaddyTypeNames = new()
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

    internal static readonly Dictionary<int, string> PickupTypeNames = new()
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

    internal static readonly Dictionary<int, string> CameraModeNames = new()
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

    internal static readonly Dictionary<int, string> TerrainTypeNames = new()
    {
        [0] = "Concrete",
        [1] = "Metal",
        [2] = "Wood"
    };
}
