using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawPs2ReplayEngine
{
    public static List<ThawReplayBatch> ReplayBatches(
        byte[] data,
        int chainStart,
        int chainEnd,
        IReadOnlyList<int> setupStarts)
    {
        var batches = new List<ThawReplayBatch>();
        var state = new VifReplayState();
        var memory = new Vu1Memory();
        var builder = new ReplayBatchBuilder();
        var position = chainStart;
        var inInterleaved = false;
        var collectingPostBatch = false;
        var currentSetupIndex = 0;
        var nextBoundaryIndex = 0;
        var firstSetupStart = setupStarts.Count > 0 ? setupStarts[0] : chainStart;

        while (position < chainEnd && position + 4 <= data.Length)
        {
            while (nextBoundaryIndex < setupStarts.Count && position >= setupStarts[nextBoundaryIndex])
            {
                nextBoundaryIndex++;
                currentSetupIndex = nextBoundaryIndex;
            }

            var cmd = data[position + 3];
            var imm = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            var command = cmd & 0x7F;
            var before = state.Snapshot();
            if (builder.FirstCommandOffset < 0)
                builder.FirstCommandOffset = position;

            if (command == 0x01)
            {
                var cl = data[position];
                var wl = data[position + 1];
                state.SetCycle(cl, wl);
                if (cl == 3 && wl == 1)
                {
                    inInterleaved = true;
                    collectingPostBatch = false;
                }
                else if (cl == 1 && wl == 1)
                {
                    inInterleaved = false;
                    collectingPostBatch = builder.HasVertices;
                }
                else
                {
                    inInterleaved = false;
                    collectingPostBatch = false;
                }

                builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stcycl, before,
                    state.Snapshot()));
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            switch (command)
            {
                case 0x03:
                    state.SetBase(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Base, before,
                        state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x02:
                    state.SetOffset(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Offset, before,
                        state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x04:
                    state.SetItop(imm & 0x3FF);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Itop, before,
                        state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x05:
                    state.SetStmod(imm & 3);
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stmod, before,
                        state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x20:
                    if (position + 8 <= data.Length)
                        state.SetStmask(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(position + 4)));
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Stmask, before,
                        state.Snapshot()));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x10:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Flush, before,
                        before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x11:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Flushe, before,
                        before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x50:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Direct, before,
                        before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x51:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.DirectHl, before,
                        before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
                case 0x4A:
                    builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Mpg, before,
                        before));
                    position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                    continue;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var unpack = ApplyUnpack(data, position, cmd, imm, state, memory, chainEnd);
                builder.Unpacks.Add(unpack);

                if (builder.FirstCommandOffset < 0)
                    builder.FirstCommandOffset = position;

                if (inInterleaved && unpack.Num > 1)
                {
                    if (unpack.Vn == 2 && unpack.Vl == 1)
                    {
                        builder.PositionOffset = unpack.DataOffset;
                        builder.PositionUnpack = unpack;
                        builder.VertexCount = unpack.Num;
                    }
                    else if (unpack.Vn == 2 && unpack.Vl == 2)
                    {
                        builder.NormalOffset = unpack.DataOffset;
                        builder.NormalUnpack = unpack;
                    }
                    else if (unpack.Vn == 3 && unpack.Vl == 1)
                    {
                        builder.UvAdcOffset = unpack.DataOffset;
                        builder.UvAdcUnpack = unpack;
                    }
                }
                else if (!collectingPostBatch && unpack.Usn && unpack.Num > 0)
                {
                    builder.ContextWrites.Add(CreateContextWrite(memory, unpack));
                }
                else if (collectingPostBatch && unpack.Vn == 3 && unpack.Vl == 1 && unpack.Num > 0)
                {
                    for (var i = 0; i < unpack.Num; i++)
                    {
                        var elementOffset = unpack.DataOffset + i * 8;
                        if (elementOffset + 8 > data.Length)
                            break;

                        builder.PostBatchElements.Add(new PostBatchElement(
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 2)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 4)),
                            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(elementOffset + 6))));
                    }
                }

                builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Unpack, before,
                    state.Snapshot(), unpack));
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            if (command is 0x14 or 0x15 or 0x17)
            {
                var outputKickPacket = DecodeOutputKickPacket(builder.PostBatchElements);
                var snapshot = state.CompleteBatch(
                    memory,
                    position,
                    imm,
                    currentSetupIndex,
                    outputKickPacket,
                    builder.Unpacks);
                builder.Commands.Add(CreateCommandTrace(
                    position,
                    imm,
                    cmd,
                    command switch
                    {
                        0x14 => VifReplayCommandKind.Mscal,
                        0x15 => VifReplayCommandKind.Mscalf,
                        _ => VifReplayCommandKind.Mscnt
                    },
                    before,
                    state.Snapshot()));

                if (builder.HasReplayActivity)
                {
                    batches.Add(builder.Build(
                        data,
                        memory,
                        currentSetupIndex,
                        snapshot,
                        builder.FirstCommandOffset >= 0 && builder.FirstCommandOffset < firstSetupStart));
                }

                builder = new ReplayBatchBuilder();
                inInterleaved = false;
                collectingPostBatch = false;
                position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
                continue;
            }

            builder.Commands.Add(CreateCommandTrace(position, imm, cmd, VifReplayCommandKind.Unknown, before, before));
            position = ThawPs2SkinFile.VifNextCode(data, position, chainEnd);
        }

        if (builder.HasReplayActivity)
        {
            var outputKickPacket = DecodeOutputKickPacket(builder.PostBatchElements);
            var snapshot = state.CompleteBatch(
                memory,
                chainEnd,
                0,
                currentSetupIndex,
                outputKickPacket,
                builder.Unpacks);
            batches.Add(builder.Build(
                data,
                memory,
                currentSetupIndex,
                snapshot,
                builder.FirstCommandOffset >= 0 && builder.FirstCommandOffset < firstSetupStart));
        }

        return batches;
    }

    internal static GifKickPacket DecodeXtopParserTag(Vu1Qword firstWord)
    {
        var nloop = (int)(firstWord.X & 0x7FFF);
        var eop = ((firstWord.X >> 15) & 1) != 0;
        var address = (int)(firstWord.Y & 0xFFFF);
        var size = (int)(firstWord.W & 0xFFFF);

        if (nloop == 0 && address == 0 && size == 0)
            return GifKickPacket.None;

        return new GifKickPacket
        {
            Kind = GifKickPacketKind.XtopWindow,
            Eop = eop,
            Nloop = nloop,
            Address = address,
            Size = size,
            SourceElementIndex = 0
        };
    }

    internal static GifKickPacket DecodeOutputKickPacket(IReadOnlyList<PostBatchElement> postBatchElements)
    {
        if (postBatchElements.Count == 0)
            return GifKickPacket.None;

        var first = postBatchElements[0];
        if ((first.C0 & 0x8000) != 0)
        {
            return new GifKickPacket
            {
                Kind = GifKickPacketKind.PostBatchFirstWord,
                Eop = (first.C0 & 0x8000) != 0,
                Nloop = first.C0 & 0x7FFF,
                Address = first.C1,
                Size = first.C3,
                SourceElementIndex = 0
            };
        }

        if ((first.C2 & 0x8000) != 0)
        {
            return new GifKickPacket
            {
                Kind = GifKickPacketKind.PostBatchSecondPair,
                Eop = (first.C2 & 0x8000) != 0,
                Nloop = first.C2 & 0x7FFF,
                Address = first.C3,
                Size = 0,
                SourceElementIndex = 0
            };
        }

        return GifKickPacket.None;
    }

    private static VifUnpackCommand ApplyUnpack(
        byte[] data,
        int commandOffset,
        byte cmd,
        ushort imm,
        VifReplayState state,
        Vu1Memory memory,
        int chainEnd)
    {
        var vn = (cmd >> 2) & 3;
        var vl = cmd & 3;
        var num = data[commandOffset + 2];
        var address = imm & 0x3FF;
        var flg = ((imm >> 15) & 1) != 0;
        var usn = ((imm >> 14) & 1) != 0;
        var dataOffset = commandOffset + 4;
        var effectiveAddress = flg ? (address + state.Tops) % Vu1Memory.SizeQwords : address % Vu1Memory.SizeQwords;

        var componentCount = vn + 1;
        var componentBits = 32 >> vl;
        var elementBytes = (componentBits * componentCount + 7) / 8;
        var source = dataOffset;
        var currentAddress = effectiveAddress;
        var cyclePosition = 0;
        var writeAddresses = new int[num];

        for (var i = 0; i < num; i++)
        {
            Span<uint> words = stackalloc uint[4];
            if (vl == 0)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source + 4 <= data.Length && source + 4 <= chainEnd)
                        words[component] = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(source));
                    source += 4;
                }
            }
            else if (vl == 1)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source + 2 <= data.Length && source + 2 <= chainEnd)
                    {
                        var raw = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(source));
                        words[component] = usn ? raw : SignExtendToUInt32(raw, 16);
                    }

                    source += 2;
                }
            }
            else if (vl == 2)
            {
                for (var component = 0; component < componentCount; component++)
                {
                    if (source < data.Length && source < chainEnd)
                    {
                        var raw = data[source];
                        words[component] = usn ? raw : SignExtendToUInt32(raw, 8);
                    }

                    source += 1;
                }
            }

            source = dataOffset + (i + 1) * elementBytes;
            var wrappedAddress = (currentAddress % Vu1Memory.SizeQwords + Vu1Memory.SizeQwords) % Vu1Memory.SizeQwords;
            writeAddresses[i] = wrappedAddress;
            memory.WriteQword(wrappedAddress, words[0], words[1], words[2], words[3]);

            cyclePosition++;
            if (state.Cl <= 1 || state.Cl == state.Wl)
            {
                currentAddress++;
            }
            else if (cyclePosition >= state.Wl)
            {
                currentAddress += 1 + (state.Cl - state.Wl);
                cyclePosition = 0;
            }
            else
            {
                currentAddress++;
            }
        }

        return new VifUnpackCommand
        {
            CommandOffset = commandOffset,
            DataOffset = dataOffset,
            Vn = vn,
            Vl = vl,
            Num = num,
            Address = address,
            Flg = flg,
            Usn = usn,
            CycleCl = state.Cl,
            CycleWl = state.Wl,
            EffectiveAddress = effectiveAddress,
            EndAddress = currentAddress % Vu1Memory.SizeQwords,
            WriteAddresses = writeAddresses
        };
    }

    private static ReplayContextWrite CreateContextWrite(Vu1Memory memory, VifUnpackCommand unpack)
    {
        var words = new Vu1Qword[unpack.WriteAddresses.Length];
        for (var i = 0; i < unpack.WriteAddresses.Length; i++)
            words[i] = memory.ReadQword(unpack.WriteAddresses[i]);

        return new ReplayContextWrite
        {
            Unpack = unpack,
            WriteAddresses = [.. unpack.WriteAddresses],
            Words = words
        };
    }

    private static uint SignExtendToUInt32(uint value, int bits)
    {
        var mask = 1u << (bits - 1);
        if ((value & mask) == 0)
            return value;

        var signed = (int)(value | (~0u << bits));
        return unchecked((uint)signed);
    }

    private static VifReplayCommandTrace CreateCommandTrace(
        int commandOffset,
        ushort imm,
        byte rawCommand,
        VifReplayCommandKind kind,
        VifReplayRegisters before,
        VifReplayRegisters after,
        VifUnpackCommand? unpack = null)
    {
        return new VifReplayCommandTrace
        {
            CommandOffset = commandOffset,
            Imm = imm,
            RawCommand = rawCommand,
            Kind = kind,
            Before = before,
            After = after,
            Unpack = unpack
        };
    }

}
