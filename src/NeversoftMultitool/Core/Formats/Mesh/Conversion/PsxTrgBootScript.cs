using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Resolves a level's model bank the way the engine's boot script does.
///
///     RE 2026-08-04 against the matched THPS2 decomp. Two independent commands
///     are easy to conflate: <c>0x80 SpoolEnv</c> in a RESTART node names the
///     GEOMETRY REGION (<c>&lt;base&gt;_2</c>), while <c>0x8E SetObjFile</c> in the
///     BOOT script names the MODEL BANK. <c>ExecuteCommandList</c>
///     (<c>TRIG.cpp:2108-2115</c>, perfect match) stores that string in
///     <c>pCurrentObjFile</c>, the only thing later PLATFORM/CBackground
///     instancing resolves against (<c>PLATFORM.cpp:195</c>,
///     <c>BACKGRND.cpp:120</c>). It is written nowhere else in the binary, so a
///     boot script with no <c>0x8E</c> genuinely leaves the level with no bank —
///     a RESTART node never names one.
///
///     Which boot script runs is decided by <c>Trig_InitialParseTRGFile</c>
///     (<c>TRIG.cpp:2800-2873</c>, perfect 130/130): when two players are active
///     it looks for AUTOEXEC2 (node type 15) and, <b>if any exists, runs those
///     INSTEAD of AUTOEXEC</b> — the scripts replace each other rather than
///     stacking. Only when no AUTOEXEC2 node exists does the one-player AUTOEXEC
///     (type 4) run.
///
///     H-O-R-S-E counts as two-player: <c>LaunchTheDamnGame</c>
///     (<c>FRONTEND2.cpp:220-224</c>) sets <c>GNumberOfPlayers = 2</c> for every
///     mode except 1-3, and <c>GGame == 7</c> is HORSE — which is why the binary
///     is littered with <c>GNumberOfPlayers == 2 &amp;&amp; GGame != 7</c> guards
///     that would be redundant otherwise.
/// </summary>
internal static class PsxTrgBootScript
{
    /// <summary><c>0x8E SetObjFile</c> — operand is an inline NUL-terminated name.</summary>
    private const int SetObjFileOpcode = 0x8E;

    /// <summary>
    ///     The bank the boot script selects for this mode.
    ///     <paramref name="BankName" /> is empty when the script names none,
    ///     which is a FAITHFUL result rather than a failure: THPS1/THPS2
    ///     <c>skdown_2</c> and THPS2 <c>skbul_2</c>/<c>skmar_2</c>/<c>skven_2</c>
    ///     ship an AUTOEXEC2 that deliberately omits <c>SetObjFile</c>, so those
    ///     regions run with no object bank at all even though an unreferenced
    ///     <c>o2</c> bank sits on the disc next to them.
    /// </summary>
    internal readonly record struct BankSelection(string BankName)
    {
        internal bool NamesBank => BankName.Length > 0;
    }

    /// <summary>
    ///     Runs the boot-script selection and returns what it names.
    ///     Returns <c>false</c> only when the file contains NO boot script at
    ///     all — the one case where a caller has nothing to go on and may fall
    ///     back to a heuristic. A boot script that names no bank returns
    ///     <c>true</c> with an empty <see cref="BankSelection.BankName" />,
    ///     because "no bank" is the engine's answer, not a missing one.
    /// </summary>
    /// <param name="trg">The level's shared <c>&lt;base&gt;_t.trg</c>.</param>
    /// <param name="twoPlayer">
    ///     True for the two-player and HORSE regions (both run with
    ///     <c>GNumberOfPlayers == 2</c>), false for the one-player region.
    /// </param>
    internal static bool TryResolveBank(TrgFile? trg, bool twoPlayer, out BankSelection selection)
    {
        selection = default;
        if (trg == null)
            return false;

        var bootNodes = SelectBootNodes(trg, twoPlayer);
        if (bootNodes.Count == 0)
            return false;

        // Every selected node executes in order; each 0x8E overwrites
        // pCurrentObjFile, so the LAST one executed is what survives into the
        // instancing code.
        var bankName = "";
        foreach (var command in bootNodes
                     .Where(static node => node.Commands != null)
                     .SelectMany(static node => node.Commands!))
        {
            if (command.Opcode != SetObjFileOpcode)
                continue;
            if (command.Args is not { Count: > 0 })
                continue;
            if (command.Args[0] is string name && name.Length > 0)
                bankName = name;
        }

        selection = new BankSelection(bankName);
        return true;
    }

    /// <summary>
    ///     <c>Trig_InitialParseTRGFile</c>'s selection, verbatim: AUTOEXEC2 wins
    ///     outright in two-player when present, otherwise AUTOEXEC runs.
    /// </summary>
    private static List<TrgNode> SelectBootNodes(TrgFile trg, bool twoPlayer)
    {
        if (twoPlayer)
        {
            var autoexec2 = trg.Nodes
                .Where(static node => node.TypeId == TrgNodeMetadata.TypeAutoexec2)
                .ToList();
            if (autoexec2.Count > 0)
                return autoexec2;
        }

        return trg.Nodes
            .Where(static node => node.TypeId == TrgNodeMetadata.TypeAutoexec)
            .ToList();
    }
}
