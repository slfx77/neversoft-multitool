namespace NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Replay;

internal sealed class VifReplayState
{
    public byte Cl { get; private set; } = 1;
    public byte Wl { get; private set; } = 1;
    public int Base { get; private set; }
    public int Offset { get; private set; }
    public int Tops { get; private set; }
    public int Top { get; private set; }
    public int Dbf { get; private set; }
    public int Itop { get; private set; }
    public int Stmod { get; private set; }
    public uint Stmask { get; private set; }

    public void SetCycle(byte cl, byte wl)
    {
        Cl = cl;
        Wl = wl;
    }

    public void SetBase(int baseAddress)
    {
        Base = baseAddress % Vu1Memory.SizeQwords;
    }

    public void SetOffset(int offset)
    {
        Offset = offset % Vu1Memory.SizeQwords;
        Dbf = 0;
        Tops = Base;
    }

    public void SetItop(int itop)
    {
        Itop = itop % Vu1Memory.SizeQwords;
    }

    public void SetStmod(int stmod)
    {
        Stmod = stmod & 3;
    }

    public void SetStmask(uint stmask)
    {
        Stmask = stmask;
    }

    public VifReplayRegisters Snapshot()
    {
        return new VifReplayRegisters(Cl, Wl, Base, Offset, Tops, Top, Dbf, Itop, Stmod, Stmask);
    }

    public Vu1BatchSnapshot CompleteBatch(
        Vu1Memory memory,
        int commandOffset,
        ushort imm,
        int setupIndex,
        GifKickPacket outputKickPacket,
        IReadOnlyList<VifUnpackCommand> unpacks)
    {
        var preTops = Tops;
        Top = Tops;
        Dbf ^= 1;
        Tops = Dbf != 0 ? (Base + Offset) % Vu1Memory.SizeQwords : Base;

        var minWritten = Top;
        var maxWritten = Top;
        var minWriteWindowStart = -1;
        Vu1Qword[] minWriteWindow = [];
        if (unpacks.Count > 0)
        {
            minWritten = unpacks[0].EffectiveAddress;
            maxWritten = (unpacks[0].EndAddress - 1 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            for (var i = 1; i < unpacks.Count; i++)
            {
                if (unpacks[i].EffectiveAddress < minWritten)
                    minWritten = unpacks[i].EffectiveAddress;

                var unpackMax = (unpacks[i].EndAddress - 1 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
                if (unpackMax > maxWritten)
                    maxWritten = unpackMax;
            }

            minWriteWindowStart = (minWritten - 2 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            minWriteWindow = memory.ReadWindow(minWriteWindowStart, 6);
        }

        var xtopWindow = memory.ReadWindow(Top, 16);
        var kickBaseWindowStart = -1;
        Vu1Qword[] kickBaseWindow = [];
        if (outputKickPacket.IsPresent)
        {
            kickBaseWindowStart = (outputKickPacket.Address - 17 + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            kickBaseWindow = memory.ReadWindow(kickBaseWindowStart, 18);
        }

        return new Vu1BatchSnapshot
        {
            SetupIndex = setupIndex,
            CommandOffset = commandOffset,
            Imm = imm,
            PreTops = preTops,
            Xtop = Top,
            PostTops = Tops,
            Dbf = Dbf,
            MinWrittenAddress = minWritten,
            MaxWrittenAddress = maxWritten,
            MinWriteWindowStart = minWriteWindowStart,
            XtopWindow = xtopWindow,
            MinWriteWindow = minWriteWindow,
            KickBaseWindowStart = kickBaseWindowStart,
            KickBaseWindow = kickBaseWindow,
            ParserTag = ThawPs2ReplayEngine.DecodeXtopParserTag(xtopWindow[0]),
            Unpacks = [.. unpacks]
        };
    }
}
