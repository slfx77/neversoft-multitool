using System.Text;

namespace NeversoftMultitool.Core.Formats.Trg;

/// <summary>
///     Disassembles TRG command lists (AUTOEXEC, COMMANDPOINT, RESTART nodes)
///     and bytecode scripts (BADDY, SCRIPTPOINT nodes).
/// </summary>
public static class TrgCommandList
{
    // --- Command list opcodes (sequential uint16 + variable args, terminated by 0xFFFF) ---
    private static readonly Dictionary<int, string> CommandNames = new()
    {
        [2] = "SetCheatRestarts",
        [3] = "SendPulse",
        [4] = "SendActivate",
        [5] = "SendSuspend",
        [10] = "SendSignal",
        [11] = "SendKill",
        [12] = "SendKillLoudly",
        [13] = "SendVisible",
        [0x66] = "WaterEffectOn",
        [0x67] = "WaterEffectOff",
        [0x68] = "SetFoggingParams",
        [0x69] = "PlaySound",
        [0x6A] = "StopSound",
        [0x6E] = "ClearAllPSXs",
        [0x7E] = "SpoolIn",
        [0x7F] = "SpoolOut",
        [0x80] = "SpoolEnv",
        [0x81] = "SpoolLock",
        [0x82] = "SetCamAngle",
        [0x83] = "BackgroundOn",
        [0x84] = "BackgroundOff",
        [0x85] = "KillEverythingInBox",
        [0x86] = "SetInitialPulses",
        [0x87] = "SetCamDistXZ",
        [0x88] = "AllowXA",
        [0x89] = "DisallowXA",
        [0x8A] = "SeekXA",
        [0x8B] = "PlayXA",
        [0x8C] = "SetRestart",
        [0x8D] = "SetVisibilityInBox",
        [0x8E] = "SetObjFile",
        [0x8F] = "SetCamDistY",
        [0x90] = "SetCamOffsetX",
        [0x91] = "SetCamOffsetY",
        [0x92] = "SetCamOffsetZ",
        [0x93] = "SetGameLevel",
        [0x94] = "IfPulseCount",
        [0x95] = "Endif",
        [0x96] = "MIDIChannelMute",
        [0x97] = "SetDualBufferSize",
        [0x98] = "KillBruce",
        [0x99] = "SetCamColijSide",
        [0x9A] = "SetCamColijBack",
        [0x9B] = "MIDIFadeIn",
        [0x9C] = "MIDIFadeOut",
        [0x9D] = "SetReverbType",
        [0x9E] = "EndLevel",
        [0x9F] = "SpoolMidi",
        [0xA0] = "SetCamMode",
        [0xA1] = "SetComplexMode",
        [0xA2] = "ClearAllCodeModules",
        [0xA3] = "IgnoreBruceInput",
        [0xA4] = "SetCamColijAngleSide",
        [0xA5] = "SetCamColijAngleBack",
        [0xA6] = "SetOTPushback",
        [0xA7] = "SetCamZoom",
        [0xA8] = "SetCamPitchDamp",
        [0xA9] = "SetOTPushback2",
        [0xAA] = "SetSuspendDistance",
        [0xAB] = "BackgroundCreate",
        [0xAC] = "SetCamYDamp",
        [0xAD] = "SetCamFocusEqualsTripod",
        [0xAE] = "SetDropDamageOn",
        [0xAF] = "SetDropDamageOff",
        [0xB0] = "SetRestart2",
        [0xB1] = "IgnoreBruceInputFreeze",
        [0xB2] = "SetRestart2Player",
        [0xB4] = "SetSpideyCamValue",
        [0xB5] = "LoadNewTrg",
        [0xB6] = "SetSpideyRGB",
        [0xB7] = "SetSpideyLookAroundCamValue",
        [0xB8] = "SetSpideyShadowRGB",
        [0xBA] = "SetCamFixedPos",
        [0xBB] = "SetCamAngleLock",
        [0xBC] = "SetCamFixedFocus",
        [0xBD] = "SpoolCodeModule",
        [0xBE] = "RunCinema",
        [0xBF] = "SetVisibilityByName",
        [0xC0] = "SetBaddyVisibilityInBox",
        [0xC1] = "SetObjectVisibilityInBox",
        [0xC2] = "SendPushback",
        [0xC3] = "WideScreen",
        [0xC4] = "BuzzSpideySense",
        [0xC5] = "SetMotionAngleOffset",
        [0xC6] = "TextMessage",
        [0xC7] = "CutSceneScript",
        [0xC8] = "SetFadeColor",
        [0xC9] = "GapPolyHit",
        [0xCA] = "SetSkyColor",
        [0xCB] = "SetCareerFlag",
        [0xCC] = "IfCareerFlag",
        [0xD1] = "SetVisibilityInBoxSM",
        [0xD9] = "AllowCamLOSCheck",
        [0xDA] = "ClearTextMessages",
        [0xDB] = "ClearTextBoxes",
        [0xDC] = "VenomEnterWaitState",
        [0xDD] = "VenomExitWaitState",
        [0xDE] = "CutSceneSkipAllow",
        [0xDF] = "SetSpideyFootStepBank",
        [0x12C] = "AllowSpeedup",
        [0x12D] = "DisallowSpeedup",
        [0x12E] = "EndLevelNode",
        [0x12F] = "FadePalettesUp",
        [0x130] = "FadePalettesDown",
        [0x131] = "KillEverything"
    };

    // Commands that take a null-terminated string argument
    private static readonly HashSet<int> StringCommands =
        [0x7E, 0x7F, 0x80, 0x8C, 0x8E, 0x9F, 0xB0, 0xB2, 0xB5, 0xBD, 0xBF, 0xC6];

    // Commands that take no arguments
    private static readonly HashSet<int> NoArgCommands =
    [
        3, 4, 5, 10, 11, 12, 0x66, 0x67, 0x6E, 0x81, 0x88, 0x89, 0x8A, 0x95,
        0x98, 0x9A, 0x9E, 0xA2, 0xAD,
        0xAF, 0xB1, 0xB6, 0xB7, 0xB8, 0xBA, 0xBB, 0xBC, 0xC7, 0xDA,
        0xDB, 0xDC, 0xDD, 0x12C, 0x12D, 0x130, 0x131
    ];

    // Commands that take one uint16 argument
    private static readonly HashSet<int> OneArgCommands =
    [
        13, 0x69, 0x6A, 0x83, 0x84, 0x86, 0x93, 0x94, 0x96, 0x97, 0x99,
        0x9B, 0x9C, 0x9D, 0xA0, 0xA1, 0xA3, 0xA4, 0xA5, 0xA6, 0xA8, 0xA9,
        0xAA, 0xAC, 0xBE, 0xC3, 0xC5, 0xCB, 0xCC,
        0xD9, 0xDE, 0xDF, 0x12E
    ];

    // Commands that take two uint16 arguments
    private static readonly HashSet<int> TwoArgCommands =
        [0x82, 0x87, 0x8B, 0x8F, 0x90, 0x91, 0x92, 0xA7, 0xAE, 0xC8, 0xCA];

    // Commands that take three uint16 arguments
    private static readonly HashSet<int> ThreeArgCommands =
        [0x68, 0x12F];

    // --- Bytecode script opcodes (0x21xx = set variable, 0x42xx+ = commands) ---
    // Decompiled from CBaddy::ExecuteCommand, CBaddy::SetVariable, CBaddy::GetVariable,
    // CPlatform::ExecuteCommand, CPlatform::SetVariable, CPlatform::GetVariable
    private static readonly Dictionary<int, string> ScriptOpNames = new()
    {
        // Value/variable opcodes (0x21xx)
        [0x2100] = "V_HEALTH",
        [0x2101] = "V_DAMAGE",
        [0x2114] = "V_VARIABLE_0",
        [0x2115] = "V_VARIABLE_1",
        [0x2120] = "V_REGISTER",
        [0x2121] = "V_HEIGHT",
        [0x2122] = "V_TIMER",
        [0x2123] = "V_APPLY_GRAVITY",
        [0x2124] = "V_MODEL_INDEX",
        [0x2125] = "V_ATTRIBUTE",
        [0x2127] = "V_ANGULAR_VELOCITY",
        [0x2128] = "V_ANGULAR_ACCELERATION",
        [0x2129] = "V_RANDOM",
        [0x212A] = "V_MY_NODE",
        [0x212B] = "V_LINKED_NODE",
        [0x212C] = "V_INPUT_SIGNAL",
        [0x212D] = "V_FACING",
        [0x212E] = "V_COUNTER",
        [0x212F] = "V_MODEL_CHECKSUM",
        [0x2131] = "V_GRAVITY_FLAG",
        [0x2132] = "V_BRUCE_XZ_DIST",
        [0x2133] = "V_LINE_OF_SIGHT",
        [0x2134] = "V_VELOCITY",
        [0x2135] = "V_CHECKSUM_2",
        [0x2136] = "V_FMV_FRAME",
        [0x2137] = "V_ANGLES",
        [0x2140] = "V_POS_X",
        [0x2141] = "V_POS_Y",
        [0x2142] = "V_POS_Z",
        [0x2150] = "V_PLAYER_X",
        [0x2151] = "V_PLAYER_Y",
        [0x2152] = "V_PLAYER_Z",
        [0x2200] = "V_COLLISION_COUNT",
        // Command opcodes (0x41xx-0x45xx)
        [0x4100] = "C_DONE",
        [0x4101] = "C_GOTO",
        [0x4102] = "C_GOTO_BREAK",
        [0x4104] = "C_LABEL",
        [0x4105] = "C_READ_LABELS",
        [0x4106] = "C_MOVE_TO_POSITION",
        [0x4107] = "C_STOP",
        [0x4110] = "C_ADD",
        [0x4112] = "C_IF_GT",
        [0x4113] = "C_IF_LT",
        [0x4114] = "C_IF_EQ",
        [0x4115] = "C_IF_FLAG_SET",
        [0x4116] = "C_IF_FLAG_CLEAR",
        [0x4120] = "C_ENDIF",
        [0x4200] = "C_LOAD_MODEL",
        [0x4201] = "C_CYCLE_ANIM",
        [0x4202] = "C_RUN_ANIM",
        [0x4203] = "C_DISPLAY_ON",
        [0x4204] = "C_DISPLAY_OFF",
        [0x4205] = "C_DIE_QUIETLY",
        [0x4220] = "C_MOVE_TO_ABSOLUTE",
        [0x4221] = "C_MOVE_TO_NODE",
        [0x4222] = "C_MOVE_RELATIVE",
        [0x4226] = "C_ZERO_VELOCITY",
        [0x4227] = "C_SET_BOUNCE_ANIM",
        [0x4240] = "C_RESET_INPUT",
        [0x4280] = "C_WAIT",
        [0x4281] = "C_WAIT_FOR_SIGNAL",
        [0x4290] = "C_PLAY_SFX",
        [0x4291] = "C_PLAY_POSITIONAL_SFX",
        [0x4292] = "C_SPARK",
        [0x4293] = "C_SET_FMV_CHECKSUM",
        [0x4294] = "C_START_FMV",
        [0x4295] = "C_NOP",
        [0x4296] = "C_MIDI_FADE_IN",
        [0x4297] = "C_MIDI_FADE_OUT",
        [0x4298] = "C_SHAKE_CAMERA",
        [0x4299] = "C_SET_FMV_TRACK",
        [0x429A] = "C_MOVE_TO_NODE_EX",
        [0x429B] = "C_SMOKEJET_ON",
        [0x429C] = "C_FLASH_SCREEN",
        [0x429D] = "C_NOP_FR",
        [0x429E] = "C_SET_WATER_LEVEL",
        [0x429F] = "C_SMOKEJET_OFF",
        [0x42A0] = "C_CREATE_POWERUP",
        [0x42A2] = "C_SET_WATER_DAMAGE",
        [0x42A6] = "C_MOVE_TO_SELF",
        [0x42B0] = "C_TEXT_MESSAGE",
        [0x42B1] = "C_SEND_PULSE_TO_LINKS",
        [0x42B2] = "C_SEND_PULSE_TO_LINKS_B",
        [0x42B3] = "C_SEND_SIGNAL_TO_NODE",
        [0x42B4] = "C_SEND_PULSE_TO_NODE",
        [0x42C0] = "C_GOALCOUNTER",
        [0x4300] = "C_WAIT_FOR_COLLISION",
        [0x4301] = "C_SHATTER",
        [0x4302] = "C_SET_ROTATION",
        [0x4303] = "C_SET_MOVE_SPEED",
        [0x4304] = "C_SCALE_ALL",
        [0x4305] = "C_SET_SOLID",
        [0x4306] = "C_SCALE_X",
        [0x4307] = "C_SCALE_Y",
        [0x4308] = "C_SCALE_Z",
        [0x4309] = "C_IS_BOUNCY",
        [0x4503] = "C_MAKE_RAIN",
        [0x4507] = "C_PLAY_LOOPING_SFX",
        [0x4508] = "C_PLAY_LOOPING_POSITIONAL_SFX",
        [0x4509] = "C_STOP_LOOPING_SFX"
    };

    /// <summary>
    ///     Parses a command list (used by AUTOEXEC, COMMANDPOINT, RESTART nodes).
    ///     Sequential uint16 opcodes with variable arguments, terminated by 0xFFFF.
    /// </summary>
    public static List<TrgCommand> ParseCommandList(BinaryReader reader, int maxBytes)
    {
        var commands = new List<TrgCommand>();
        var endPos = reader.BaseStream.Position + maxBytes;

        while (reader.BaseStream.Position + 2 <= endPos)
        {
            var opcode = reader.ReadUInt16();
            if (opcode == 0xFFFF)
                break;

            var name = CommandNames.GetValueOrDefault(opcode, $"Unknown_0x{opcode:X4}");
            var args = new List<object>();

            if (StringCommands.Contains(opcode))
            {
                args.Add(ReadString(reader));
            }
            else if (NoArgCommands.Contains(opcode))
            {
                // No arguments
            }
            else if (OneArgCommands.Contains(opcode))
            {
                if (reader.BaseStream.Position + 2 <= endPos)
                    args.Add(reader.ReadUInt16());
            }
            else if (TwoArgCommands.Contains(opcode))
            {
                for (var i = 0; i < 2 && reader.BaseStream.Position + 2 <= endPos; i++)
                    args.Add(reader.ReadUInt16());
            }
            else if (ThreeArgCommands.Contains(opcode))
            {
                for (var i = 0; i < 3 && reader.BaseStream.Position + 2 <= endPos; i++)
                    args.Add(reader.ReadUInt16());
            }
            else if (opcode == 0x85) // KillEverythingInBox — 6 int32 values (bounding box)
            {
                for (var i = 0; i < 6 && reader.BaseStream.Position + 4 <= endPos; i++)
                    args.Add(reader.ReadInt32() / 4096.0);
            }
            else if (opcode is 0x8D or 0xC0 or 0xC1 or 0xD1) // SetVisibilityInBox variants
            {
                if (reader.BaseStream.Position + 2 <= endPos)
                    args.Add(reader.ReadUInt16()); // type
                // Read bounding boxes until 0xFF sentinel
                while (reader.BaseStream.Position + 2 <= endPos)
                {
                    var val = reader.ReadUInt16();
                    if (val == 0x00FF)
                        break;
                    args.Add(val);
                }
            }
            else if (opcode == 0xAB) // BackgroundCreate — align4, checksum, 3 uint16
            {
                Align4(reader);
                if (reader.BaseStream.Position + 4 <= endPos)
                    args.Add($"0x{reader.ReadUInt32():X8}");
                for (var i = 0; i < 3 && reader.BaseStream.Position + 2 <= endPos; i++)
                    args.Add(reader.ReadUInt16());
            }
            else if (opcode == 0xC2) // SendPushback — align4, checksum
            {
                Align4(reader);
                if (reader.BaseStream.Position + 4 <= endPos)
                    args.Add($"0x{reader.ReadUInt32():X8}");
            }
            else if (opcode == 0xC4) // BuzzSpideySense — 2 uint16
            {
                for (var i = 0; i < 2 && reader.BaseStream.Position + 2 <= endPos; i++)
                    args.Add(reader.ReadUInt16());
            }
            else if (opcode == 0xC9) // GapPolyHit — align4, checksum, int16
            {
                Align4(reader);
                if (reader.BaseStream.Position + 4 <= endPos)
                    args.Add($"0x{reader.ReadUInt32():X8}");
                if (reader.BaseStream.Position + 2 <= endPos)
                    args.Add(reader.ReadInt16());
            }
            else if (opcode == 0xB4) // SetSpideyCamValue — 10 bytes
            {
                for (var i = 0; i < 5 && reader.BaseStream.Position + 2 <= endPos; i++)
                    args.Add(reader.ReadUInt16());
            }
            else if (opcode == 2) // SetCheatRestarts — strings until empty
            {
                while (reader.BaseStream.Position + 1 <= endPos)
                {
                    var str = ReadString(reader);
                    if (str.Length == 0) break;
                    args.Add(str);
                }
            }

            // Unknown opcode — assume 0 args (safest default based on observed data).
            // If this opcode actually has arguments, subsequent opcodes may desync.
            commands.Add(new TrgCommand
            {
                Opcode = opcode,
                Name = name,
                Args = args.Count > 0 ? args : null
            });
        }

        return commands;
    }

    /// <summary>
    ///     Parses a bytecode script (used by BADDY and SCRIPTPOINT nodes).
    ///     Stack-based: 0x21xx = value ops, 0x42xx = command ops, values below 0x2000 are literals.
    /// </summary>
    public static List<TrgScriptOp> ParseScript(BinaryReader reader, int maxBytes)
    {
        var ops = new List<TrgScriptOp>();
        var endPos = reader.BaseStream.Position + maxBytes;

        while (reader.BaseStream.Position + 2 <= endPos)
        {
            var opcode = reader.ReadUInt16();

            // C_DONE terminates the script
            if (opcode == 0x4100)
            {
                ops.Add(new TrgScriptOp
                {
                    Opcode = $"0x{opcode:X4}",
                    Name = "C_DONE"
                });
                break;
            }

            if (opcode < 0x2000)
            {
                // Literal value — push as integer
                ops.Add(new TrgScriptOp
                {
                    Opcode = $"0x{opcode:X4}",
                    Name = "LITERAL",
                    Value = opcode
                });
                continue;
            }

            var name = ScriptOpNames.GetValueOrDefault(opcode, $"Unknown_0x{opcode:X4}");
            var op = new TrgScriptOp
            {
                Opcode = $"0x{opcode:X4}",
                Name = name
            };

            // Parse arguments based on opcode
            switch (opcode)
            {
                case 0x212F: // V_MODEL_CHECKSUM — align4, uint32 hash
                case 0x4293: // C_SET_FMV_CHECKSUM — align4, uint32
                case 0x2135: // V_CHECKSUM_2 — align4 + uint32
                    Align4(reader);
                    if (reader.BaseStream.Position + 4 <= endPos)
                        op.Value = $"0x{reader.ReadUInt32():X8}";
                    break;

                case 0x2100: // V_HEALTH
                case 0x2101: // V_DAMAGE
                case 0x2114: // V_VARIABLE_0
                case 0x2115: // V_VARIABLE_1
                case 0x2120: // V_REGISTER — uint16 index
                case 0x2121: // V_HEIGHT
                case 0x2122: // V_TIMER
                case 0x2123: // V_APPLY_GRAVITY
                case 0x2124: // V_MODEL_INDEX
                case 0x2129: // V_RANDOM
                case 0x212D: // V_FACING
                case 0x212E: // V_COUNTER
                case 0x2131: // V_GRAVITY_FLAG
                case 0x4101: // C_GOTO — label index
                case 0x4102: // C_GOTO_BREAK — label index
                case 0x4104: // C_LABEL
                case 0x4115: // C_IF_FLAG_SET
                case 0x4116: // C_IF_FLAG_CLEAR
                case 0x4202: // C_RUN_ANIM
                case 0x4227: // C_SET_BOUNCE_ANIM
                case 0x4292: // C_SPARK
                case 0x4296: // C_MIDI_FADE_IN
                case 0x4297: // C_MIDI_FADE_OUT
                case 0x4298: // C_SHAKE_CAMERA
                case 0x4299: // C_SET_FMV_TRACK
                case 0x42A2: // C_SET_WATER_DAMAGE
                case 0x42C0: // C_GOALCOUNTER
                case 0x4303: // C_SET_MOVE_SPEED
                case 0x4305: // C_SET_SOLID
                case 0x4309: // C_IS_BOUNCY
                case 0x4503: // C_MAKE_RAIN
                    if (reader.BaseStream.Position + 2 <= endPos)
                        op.Value = reader.ReadUInt16();
                    break;

                case 0x4290: // C_PLAY_SFX — int16
                case 0x4291: // C_PLAY_POSITIONAL_SFX
                case 0x4507: // C_PLAY_LOOPING_SFX
                    if (reader.BaseStream.Position + 2 <= endPos)
                        op.Value = reader.ReadInt16();
                    break;

                case 0x4508: // C_PLAY_LOOPING_POSITIONAL_SFX — int16, uint16
                    if (reader.BaseStream.Position + 4 <= endPos)
                    {
                        var sfx = reader.ReadInt16();
                        var param = reader.ReadUInt16();
                        op.Value = new object[] { sfx, param };
                    }

                    break;

                case 0x2125: // V_ATTRIBUTE — uint16, uint16
                    if (reader.BaseStream.Position + 4 <= endPos)
                        op.Value = new object[] { reader.ReadUInt16(), reader.ReadUInt16() };
                    break;

                case 0x2127: // V_ANGULAR_VELOCITY — 3x int16
                case 0x2128: // V_ANGULAR_ACCELERATION
                case 0x2134: // V_VELOCITY
                case 0x2137: // V_ANGLES
                    if (reader.BaseStream.Position + 6 <= endPos)
                        op.Value = new object[] { reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16() };
                    break;

                case 0x429B: // C_SMOKEJET_ON — 6 uint16
                    if (reader.BaseStream.Position + 12 <= endPos)
                    {
                        var vals = new object[6];
                        for (var i = 0; i < 6; i++)
                            vals[i] = reader.ReadUInt16();
                        op.Value = vals;
                    }

                    break;

                case 0x429C: // C_FLASH_SCREEN — 5 uint16 (r,g,b,duration,sort)
                    if (reader.BaseStream.Position + 10 <= endPos)
                    {
                        var vals = new object[5];
                        for (var i = 0; i < 5; i++)
                            vals[i] = reader.ReadUInt16();
                        op.Value = vals;
                    }

                    break;

                case 0x4306: // C_SCALE_X — int16 percent, uint16 frames
                case 0x4307: // C_SCALE_Y
                case 0x4308: // C_SCALE_Z
                    if (reader.BaseStream.Position + 4 <= endPos)
                        op.Value = new object[] { reader.ReadInt16(), reader.ReadUInt16() };
                    break;

                case 0x42B0: // C_TEXT_MESSAGE — null-terminated string, align 2
                    op.Value = ReadString(reader);
                    break;

                case 0x4201: // C_CYCLE_ANIM — anim, speed
                case 0x4304: // C_SCALE_ALL — percent, frames
                    if (reader.BaseStream.Position + 4 <= endPos)
                        op.Value = new object[] { reader.ReadUInt16(), reader.ReadUInt16() };
                    break;

                case 0x4302: // C_SET_ROTATION — 3x int16 (XYZ angular speed)
                    if (reader.BaseStream.Position + 6 <= endPos)
                        op.Value = new object[] { reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16() };
                    break;

                case 0x4220: // C_MOVE_TO_ABSOLUTE — align4 + 3x int32
                case 0x4222: // C_MOVE_RELATIVE — align4 + 3x int32
                    Align4(reader);
                    if (reader.BaseStream.Position + 12 <= endPos)
                        op.Value = new object[]
                        {
                            reader.ReadInt32() / 4096.0,
                            reader.ReadInt32() / 4096.0,
                            reader.ReadInt32() / 4096.0
                        };
                    break;

                case 0x4200: // C_LOAD_MODEL — null-terminated string
                    op.Value = ReadString(reader);
                    break;

                // No-arg script opcodes
                case 0x212A: // V_MY_NODE
                case 0x212C: // V_INPUT_SIGNAL
                case 0x2132: // V_BRUCE_XZ_DIST
                case 0x2133: // V_LINE_OF_SIGHT
                case 0x2136: // V_FMV_FRAME
                case 0x2140: // V_POS_X
                case 0x2141: // V_POS_Y
                case 0x2142: // V_POS_Z
                case 0x2150: // V_PLAYER_X
                case 0x2151: // V_PLAYER_Y
                case 0x2152: // V_PLAYER_Z
                case 0x2200: // V_COLLISION_COUNT
                case 0x4105: // C_READ_LABELS
                case 0x4107: // C_STOP
                case 0x4120: // C_ENDIF
                case 0x4203: // C_DISPLAY_ON
                case 0x4204: // C_DISPLAY_OFF
                case 0x4205: // C_DIE_QUIETLY
                case 0x4226: // C_ZERO_VELOCITY
                case 0x4240: // C_RESET_INPUT
                case 0x4281: // C_WAIT_FOR_SIGNAL
                case 0x4294: // C_START_FMV
                case 0x4295: // C_NOP
                case 0x429D: // C_NOP_FR
                case 0x429E: // C_SET_WATER_LEVEL
                case 0x429F: // C_SMOKEJET_OFF
                case 0x42A6: // C_MOVE_TO_SELF
                case 0x4300: // C_WAIT_FOR_COLLISION
                case 0x4301: // C_SHATTER
                case 0x4509: // C_STOP_LOOPING_SFX
                    break;

                // Opcodes that take a script value (recursive-ish in the reference impl)
                // For now, treat as consuming one uint16
                case 0x4110: // C_ADD
                case 0x4112: // C_IF_GT
                case 0x4113: // C_IF_LT
                case 0x4114: // C_IF_EQ
                case 0x4221: // C_MOVE_TO_NODE
                case 0x4280: // C_WAIT
                case 0x212B: // V_LINKED_NODE
                case 0x42B1: // C_SEND_PULSE_TO_LINKS
                case 0x42B3: // C_SEND_SIGNAL_TO_NODE
                case 0x42B4: // C_SEND_PULSE_TO_NODE
                    if (reader.BaseStream.Position + 2 <= endPos)
                        op.Value = reader.ReadUInt16();
                    break;
            }

            ops.Add(op);
        }

        return ops;
    }

    private static string ReadString(BinaryReader reader)
    {
        var sb = new StringBuilder();
        while (true)
        {
            if (reader.BaseStream.Position >= reader.BaseStream.Length)
                break;
            var b = reader.ReadByte();
            if (b == 0) break;
            sb.Append((char)b);
        }

        // Align to 2-byte boundary
        var pos = reader.BaseStream.Position;
        if (pos % 2 != 0)
            reader.BaseStream.Position = pos + 1;

        return sb.ToString();
    }

    private static void Align4(BinaryReader reader)
    {
        var pos = reader.BaseStream.Position;
        if (pos % 4 != 0)
            reader.BaseStream.Position = pos + (4 - pos % 4);
    }
}

/// <summary>
///     A single command in a TRG command list.
/// </summary>
public sealed class TrgCommand
{
    public int Opcode { get; init; }
    public string Name { get; init; } = "";
    public List<object>? Args { get; init; }
}

/// <summary>
///     A single operation in a TRG bytecode script.
/// </summary>
public sealed class TrgScriptOp
{
    public string Opcode { get; init; } = "";
    public string Name { get; init; } = "";
    public object? Value { get; set; }
}
