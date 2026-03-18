using System.Text;

namespace NeversoftMultitool.Core.Formats.Ps2Scene;

internal static class ThawPs2ReplayTraceFormatter
{
    public static string FormatBatches(IReadOnlyList<ThawReplayBatch> batches)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < batches.Count; i++)
        {
            var batch = batches[i];
            if (i > 0)
                sb.AppendLine();

            sb.Append("BATCH ").Append(i);
            sb.Append(" setup=").Append(batch.SetupIndex);
            if (batch.IsPreambleBatch)
                sb.Append(" PREAMBLE");
            sb.AppendLine();

            sb.Append("  first=0x").Append(batch.FirstCommandOffset.ToString("X6"));
            sb.Append(" verts=").Append(batch.VertexCount);
            sb.Append(" out=").Append(batch.OutputVertexCount);
            sb.AppendLine();

            var kick = batch.OutputKickPacket;
            sb.Append("  kick=").Append(kick.Kind);
            sb.Append(" nloop=").Append(kick.Nloop);
            sb.Append(" addr=").Append(kick.Address);
            sb.Append(" size=").Append(kick.Size);
            sb.AppendLine();

            var snapshot = batch.Snapshot;
            sb.Append("  xtop=").Append(snapshot.Xtop);
            sb.Append(" preTops=").Append(snapshot.PreTops);
            sb.Append(" postTops=").Append(snapshot.PostTops);
            sb.Append(" dbf=").Append(snapshot.Dbf);
            sb.AppendLine();

            sb.Append("  minWrite=").Append(snapshot.MinWrittenAddress);
            sb.Append(" maxWrite=").Append(snapshot.MaxWrittenAddress);
            if (snapshot.MinWriteWindowStart >= 0)
                sb.Append(" minWindowStart=").Append(snapshot.MinWriteWindowStart);
            sb.AppendLine();

            var parser = snapshot.ParserTag;
            sb.Append("  parser=").Append(parser.Kind);
            sb.Append(" nloop=").Append(parser.Nloop);
            sb.Append(" addr=").Append(parser.Address);
            sb.Append(" size=").Append(parser.Size);
            sb.AppendLine();

            if (batch.ContextWrites.Length > 0)
            {
                sb.AppendLine("  context:");
                foreach (var contextWrite in batch.ContextWrites)
                    AppendContextWrite(sb, contextWrite);
            }

            if (snapshot.XtopWindow.Length > 0)
            {
                var first = snapshot.XtopWindow[0];
                sb.Append("  xtop[0]=(");
                sb.Append(first.X.ToString("X8")).Append(", ");
                sb.Append(first.Y.ToString("X8")).Append(", ");
                sb.Append(first.Z.ToString("X8")).Append(", ");
                sb.Append(first.W.ToString("X8")).Append(')');
                sb.AppendLine();
            }

            if (snapshot.MinWriteWindow.Length > 0)
            {
                for (var windowIndex = 0; windowIndex < snapshot.MinWriteWindow.Length; windowIndex++)
                {
                    var address = (snapshot.MinWriteWindowStart + windowIndex) % Vu1Memory.SizeQwords;
                    var word = snapshot.MinWriteWindow[windowIndex];
                    sb.Append("  minWindow[").Append(address).Append("]=(");
                    sb.Append(word.X.ToString("X8")).Append(", ");
                    sb.Append(word.Y.ToString("X8")).Append(", ");
                    sb.Append(word.Z.ToString("X8")).Append(", ");
                    sb.Append(word.W.ToString("X8")).Append(')');
                    sb.AppendLine();
                }
            }

            if (snapshot.KickBaseWindow.Length > 0)
            {
                for (var windowIndex = 0; windowIndex < snapshot.KickBaseWindow.Length; windowIndex++)
                {
                    var address = (snapshot.KickBaseWindowStart + windowIndex) % Vu1Memory.SizeQwords;
                    var word = snapshot.KickBaseWindow[windowIndex];
                    sb.Append("  kickBase[").Append(address).Append("]=(");
                    sb.Append(word.X.ToString("X8")).Append(", ");
                    sb.Append(word.Y.ToString("X8")).Append(", ");
                    sb.Append(word.Z.ToString("X8")).Append(", ");
                    sb.Append(word.W.ToString("X8")).Append(')');
                    sb.AppendLine();
                }
            }

            if (batch.CommandTrace.Length > 0)
            {
                sb.AppendLine("  commands:");
                foreach (var command in batch.CommandTrace)
                    sb.Append("    ").AppendLine(FormatCommand(command));
            }
        }

        return sb.ToString();
    }

    private static string FormatCommand(VifReplayCommandTrace command)
    {
        var prefix = $"[0x{command.CommandOffset:X6}] ";
        return command.Kind switch
        {
            VifReplayCommandKind.Stcycl => $"{prefix}STCYCL CL={command.After.Cl} WL={command.After.Wl}",
            VifReplayCommandKind.Base => $"{prefix}BASE = {command.After.Base}",
            VifReplayCommandKind.Offset =>
                $"{prefix}OFFSET = {command.After.Offset} (TOPS={command.After.Tops}, DBF={command.After.Dbf})",
            VifReplayCommandKind.Itop => $"{prefix}ITOP = {command.After.Itop}",
            VifReplayCommandKind.Stmod => $"{prefix}STMOD = {command.After.Stmod}",
            VifReplayCommandKind.Stmask => $"{prefix}STMASK = 0x{command.After.Stmask:X8}",
            VifReplayCommandKind.Unpack => FormatUnpack(command),
            VifReplayCommandKind.Mscal or VifReplayCommandKind.Mscalf or VifReplayCommandKind.Mscnt =>
                $"{prefix}{command.Kind.ToString().ToUpperInvariant()} imm={command.Imm} (TOP={command.After.Top}, TOPS: {command.Before.Tops}->{command.After.Tops}, DBF: {command.Before.Dbf}->{command.After.Dbf})",
            VifReplayCommandKind.Flush => $"{prefix}FLUSH",
            VifReplayCommandKind.Flushe => $"{prefix}FLUSHE",
            VifReplayCommandKind.Direct => $"{prefix}DIRECT nloop={command.Imm}",
            VifReplayCommandKind.DirectHl => $"{prefix}DIRECTHL nloop={command.Imm}",
            VifReplayCommandKind.Mpg => $"{prefix}MPG imm={command.Imm}",
            VifReplayCommandKind.Nop => $"{prefix}NOP",
            _ => $"{prefix}VIF_0x{command.RawCommand & 0x7F:X2} imm={command.Imm}"
        };
    }

    private static string FormatUnpack(VifReplayCommandTrace command)
    {
        if (command.Unpack is null)
            return $"[0x{command.CommandOffset:X6}] UNPACK";

        var unpack = command.Unpack;
        var format = GetFormatName(unpack.Vn, unpack.Vl);
        var unsigned = unpack.Usn ? "U" : string.Empty;
        var tops = unpack.Flg ? $"+TOPS({command.Before.Tops})" : string.Empty;
        return
            $"[0x{command.CommandOffset:X6}] UNPACK {unsigned}{format} NUM={unpack.Num} ADDR={unpack.Address}{tops} -> eff={unpack.EffectiveAddress} (end={unpack.EndAddress})";
    }

    private static void AppendContextWrite(StringBuilder sb, ReplayContextWrite contextWrite)
    {
        var unpack = contextWrite.Unpack;
        var format = GetFormatName(unpack.Vn, unpack.Vl);
        var unsigned = unpack.Usn ? "U" : string.Empty;
        sb.Append("    [0x").Append(unpack.CommandOffset.ToString("X6")).Append("] ");
        sb.Append("UNPACK ").Append(unsigned).Append(format);
        sb.Append(" NUM=").Append(unpack.Num);
        sb.Append(" ADDR=").Append(unpack.Address);
        sb.Append(" -> ");
        sb.Append(string.Join(", ", contextWrite.WriteAddresses.Select(address => address.ToString())));
        sb.AppendLine();

        for (var i = 0; i < contextWrite.Words.Length; i++)
        {
            var address = contextWrite.WriteAddresses[i];
            var word = contextWrite.Words[i];
            sb.Append("      ctx[").Append(address).Append("]=(");
            sb.Append(word.X.ToString("X8")).Append(", ");
            sb.Append(word.Y.ToString("X8")).Append(", ");
            sb.Append(word.Z.ToString("X8")).Append(", ");
            sb.Append(word.W.ToString("X8")).Append(')');
            sb.AppendLine();
        }
    }

    private static string GetFormatName(int vn, int vl)
    {
        return (vn, vl) switch
        {
            (0, 0) => "S_32",
            (0, 1) => "S_16",
            (0, 2) => "S_8",
            (1, 0) => "V2_32",
            (1, 1) => "V2_16",
            (1, 2) => "V2_8",
            (2, 0) => "V3_32",
            (2, 1) => "V3_16",
            (2, 2) => "V3_8",
            (3, 0) => "V4_32",
            (3, 1) => "V4_16",
            (3, 2) => "V4_8",
            _ => $"?({vn},{vl})"
        };
    }
}
