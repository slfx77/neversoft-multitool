using NeversoftMultitool.Core.Formats.Trg;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     The files a trigger names. A TRG script has to tell the engine what to
///     load, so it carries filenames as inline NUL-terminated strings — nothing
///     hashed, nothing indexed:
///     <list type="bullet">
///         <item><c>0x8E SetObjFile</c> — the level's OBJECT BANK (<c>SkVans_O</c>).</item>
///         <item><c>0x80 SpoolEnv</c> — a GEOMETRY REGION (<c>SkVans</c>, <c>SkVans_2</c>).</item>
///         <item><c>0x7E SpoolIn</c> — anything else spooled (<c>SkVans_L</c>, <c>C_Taxi</c>).</item>
///     </list>
///     <para>
///         Splitting the FAMILY from the OUTSIDERS is load-bearing, not tidiness.
///         A trigger names more than its own level — Spider-Man's <c>l1a1</c>
///         spools <c>henchman</c>, <c>blackcat</c> and <c>THUG</c>; THPS1's
///         <c>skdown</c> spools <c>c_taxi</c> — and those bundles live elsewhere
///         in the slot space. Feeding the whole set to the run aligner breaks
///         contiguity and drops Spider-Man from 55 aligned triggers to 0.
///     </para>
/// </summary>
internal static class N64TrgFileReferences
{
    private const int SpoolInOpcode = 0x7E;
    private const int SpoolEnvOpcode = 0x80;
    private const int SetObjFileOpcode = 0x8E;

    /// <summary>
    ///     Bank suffixes, longest first so <c>_o2</c> is not read as <c>_o</c>
    ///     followed by a stray digit. THREE spellings ship: THPS1 has
    ///     <c>skjamo2</c> and <c>sksf_o2</c> in the same game, and the late
    ///     Shaba THPS3 port uses a third, <c>aa&lt;stem&gt;2o</c>.
    ///     <para>
    ///         <c>2o</c> is not cosmetic. Without it <c>LevelStem("aaburb2o")</c>
    ///         returns the whole name, so the family collapses to the single
    ///         file starting with it and a one-element family aligns trivially
    ///         onto any content-anchored slot.
    ///     </para>
    /// </summary>
    private static readonly string[] BankSuffixes = ["_o2", "2o", "o2", "_o"];

    /// <summary>
    ///     Orders names the way the slot run does: LOWERCASE, then ordinal.
    ///     <para>
    ///         The distinction from <see cref="StringComparer.OrdinalIgnoreCase" />
    ///         is load-bearing and cost four slots per level when it was wrong.
    ///         OrdinalIgnoreCase compares UPPERCASED, so a letter (<c>L</c>,
    ///         0x4C) sorts before <c>_</c> (0x5F) and <c>skdownl2</c> lands ahead
    ///         of <c>skdown_2</c>. Lowercased, <c>_</c> (0x5F) precedes <c>l</c>
    ///         (0x6C) and the order matches the run — verified against THPS1's
    ///         skdown and skschl families, whose <c>_l</c> and <c>l2</c>
    ///         libraries are exactly the pair that swaps.
    ///     </para>
    /// </summary>
    private sealed class LowercaseOrdinalComparer : IComparer<string>
    {
        internal static readonly LowercaseOrdinalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            return string.CompareOrdinal(x?.ToLowerInvariant(), y?.ToLowerInvariant());
        }
    }

    /// <summary>
    ///     <paramref name="Family" /> is the level's own files, sorted the way
    ///     the slot run orders them (lowercased, then ordinal — see
    ///     <c>LowercaseOrdinalComparer</c>; this is NOT OrdinalIgnoreCase).
    ///     <paramref name="BankName" /> is empty when the script names no bank,
    ///     which is a faithful answer rather than a failure — THPS3's four
    ///     <c>sked</c> park-editor triggers name none, exactly as the PS1 ships
    ///     no <c>sked&lt;N&gt;_o</c>.
    /// </summary>
    internal readonly record struct FileSet(
        string BankName,
        IReadOnlyList<string> Family,
        IReadOnlyList<string> Outsiders)
    {
        internal bool HasFamily => Family.Count > 0;
    }

    internal static FileSet Collect(TrgFile trg)
    {
        var names = new SortedSet<string>(LowercaseOrdinalComparer.Instance);
        var bank = "";

        var fileCommands = trg.Nodes
            .Where(static node => node.Commands != null)
            .SelectMany(static node => node.Commands!)
            .Where(static command =>
                command.Opcode is SpoolInOpcode or SpoolEnvOpcode or SetObjFileOpcode
                && command.Args is { Count: > 0 });

        foreach (var command in fileCommands)
        {
            foreach (var text in command.Args!.OfType<string>().Where(IsFileName))
            {
                names.Add(text);
                // The LAST SetObjFile executed is what survives into
                // pCurrentObjFile, matching PsxTrgBootScript's rule.
                if (command.Opcode == SetObjFileOpcode)
                    bank = text;
            }
        }

        if (bank.Length == 0)
            return new FileSet("", [], names.ToArray());

        var stem = LevelStem(bank);
        var family = new List<string>();
        var outsiders = new List<string>();
        foreach (var name in names)
            (name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) ? family : outsiders).Add(name);

        return new FileSet(bank, family, outsiders);
    }

    /// <summary>
    ///     <c>l1a1_o</c> / <c>skjamo2</c> / <c>skros_o</c> → the stem the level's
    ///     slot run is built on.
    /// </summary>
    internal static string LevelStem(string bankName)
    {
        var suffix = BankSuffixes.FirstOrDefault(
            s => bankName.EndsWith(s, StringComparison.OrdinalIgnoreCase));
        return suffix == null ? bankName : bankName[..^suffix.Length];
    }

    /// <summary>
    ///     A file operand, as opposed to a checksum some other command spells as
    ///     text (<c>GapPolyHit</c> and <c>BackgroundCreate</c> both carry
    ///     <c>0x...</c> strings) or an empty slot.
    /// </summary>
    private static bool IsFileName(string text)
    {
        return text.Length > 0
               && !text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
    }
}
