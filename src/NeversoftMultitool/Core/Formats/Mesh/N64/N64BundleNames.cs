using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Globalization;
using NeversoftMultitool.Core.Formats.Mesh.Psx;

namespace NeversoftMultitool.Core.Formats.Mesh.N64;

/// <summary>
///     Real names for carved N64 model bundles, keyed by CONTENT.
///     <para>
///         The Edge of Reality ports re-encoded Neversoft's PS1 <c>.psx</c>
///         containers big-endian and stripped their geometry chunks, but the
///         object table and the QbKey mesh-name hash array survive intact — the
///         <c>models/NNN</c> bundles ARE the PS1 files. Matching a bundle's
///         mesh-name-hash SET against the PS1 corpus therefore names it.
///         Measured 2026-08-07: 433 of 450 bundles match SOME PS1 file, and
///         329 resolve to a name that is true of the content rather than a
///         pick among candidates — THPS1 59/65, THPS2 78/116, THPS3 63/87,
///         Spider-Man 129/182. <see cref="TryResolve" /> explains the gap.
///     </para>
///     <para>
///         The PS1 corpus is NOT available at runtime, so identity is harvested
///         offline into an embedded dictionary keyed by a digest the carved
///         shell alone can produce. Same shape as
///         <see cref="Texture.ThawTextureNames" /> and
///         <c>HedDictionary</c> (consumed by <c>WadArchive</c> as
///         <c>TryResolve(hash) ?? hex</c>).
///     </para>
///     <para>
///         These keys are FNV-1a digests of hash SETS, not <c>CRC(name)</c>
///         pairs, so they must never be merged into <c>QbKeyNames*.txt</c> —
///         doing so would poison the QbKey dictionary and its coverage metric.
///         The embedded table is the materialized result of the 2026-08-07
///         PS1/N64 corpus match described above.
///     </para>
///     <para>
///         AMBIGUITY IS EXPECTED. A hash set describes content, and several PS1
///         files can share content (Spider-Man's <c>l9a3_o</c>/<c>l9a4_o</c>/
///         <c>lba3_o</c>/<c>lba4_o</c> are one 4-mesh set; <c>skss_o</c> and
///         <c>skss_o2</c> are the one- and two-player banks). 95 of 987 keys
///         carry more than one candidate. <see cref="TryResolve" /> returns a
///         name only when every candidate agrees on a prefix that is itself a
///         candidate, while <see cref="ResolveAll" /> exposes the complete set;
///         the carver's slot prefix keeps the emitted path unique either way.
///     </para>
/// </summary>
public static class N64BundleNames
{
    internal readonly record struct ShellIdentity(ulong Key, IReadOnlyList<string> Candidates);

    /// <summary>
    ///     Hashes below this are ordinals and sentinels rather than QbKey name
    ///     hashes. The harvest applies the identical filter, so the key is
    ///     computed over the same population the coverage figures were measured
    ///     on — changing it on one side alone silently empties the dictionary.
    /// </summary>
    public const uint MinimumMeshNameHash = 0x1_0000;

    private const ulong FnvBasis = 0xCBF29CE484222325;
    private const ulong FnvPrime = 0x100000001B3;

    private static readonly FrozenDictionary<ulong, string[]> Names = Load();

    private static readonly string[] NoNames = [];

    /// <summary>How many content keys the dictionary carries.</summary>
    public static int Count => Names.Count;

    /// <summary>
    ///     Content digest for a mesh-name hash array: FNV-1a 64 over the
    ///     distinct qualifying hash count as four big-endian bytes, then those
    ///     hashes ascending, four big-endian bytes each.
    ///     <para>
    ///         Order- and duplicate-invariant by construction, so a shell whose
    ///         hashes were read in a different order still keys the same.
    ///         Returns null when nothing qualifies (an empty or stub bundle).
    ///     </para>
    /// </summary>
    public static ulong? ComputeKey(ReadOnlySpan<uint> meshNameHashes)
    {
        var distinct = new SortedSet<uint>();
        foreach (var hash in meshNameHashes)
        {
            if (hash >= MinimumMeshNameHash)
                distinct.Add(hash);
        }

        if (distinct.Count == 0)
            return null;

        Span<byte> word = stackalloc byte[4];
        var digest = FnvBasis;

        BinaryPrimitives.WriteUInt32BigEndian(word, (uint)distinct.Count);
        digest = Absorb(digest, word);
        foreach (var hash in distinct)
        {
            BinaryPrimitives.WriteUInt32BigEndian(word, hash);
            digest = Absorb(digest, word);
        }

        return digest;
    }

    private static ulong Absorb(ulong digest, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
            digest = (digest ^ b) * FnvPrime;
        return digest;
    }

    /// <summary>
    ///     The name for a content key, or null when content cannot name it.
    ///     <para>
    ///         A unique key returns its one candidate. An ambiguous key returns
    ///         the candidates' common prefix ONLY when that prefix is itself one
    ///         of them — <c>glif</c> for
    ///         <c>glif|glif2|glif2b|glif_fe</c>, <c>skss_o</c> for
    ///         <c>skss_o|skss_o2</c> — so the answer is a name every candidate
    ///         agrees on rather than a pick among them.
    ///     </para>
    ///     <para>
    ///         Otherwise it returns null and the bundle keeps its slot. That
    ///         matters: content identity CANNOT separate characters built on a
    ///         shared rig — every THPS2 skater has the same 19 part names AND
    ///         the same part positions, so one key covers up to 10 unrelated
    ///         files (<c>burnq2b|cab2b|hawk|hawk2|…</c>). Returning the first
    ///         would name 433 of 450 bundles at the cost of 159 arbitrary
    ///         picks; this rule names 329 and every one is true of the content.
    ///         Requiring the prefix to BE a candidate also stops it inventing a
    ///         name that is nobody's — <c>c_bus|c_bull</c> would otherwise yield
    ///         <c>c_bu</c> (21 such keys).
    ///     </para>
    /// </summary>
    public static string? TryResolve(ulong key)
    {
        if (!Names.TryGetValue(key, out var names) || names.Length == 0)
            return null;
        if (names.Length == 1)
            return names[0];

        var prefixLength = names[0].Length;
        foreach (var name in names)
        {
            prefixLength = Math.Min(prefixLength, CommonPrefixLength(names[0], name));
            if (prefixLength == 0)
                return null;
        }

        var prefix = names[0][..prefixLength];
        return Array.IndexOf(names, prefix) >= 0 ? prefix : null;
    }

    private static int CommonPrefixLength(string a, string b)
    {
        var limit = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < limit && a[i] == b[i])
            i++;
        return i;
    }

    /// <summary>
    ///     Every PS1 file whose content matches this key. More than one means
    ///     the files are genuinely indistinguishable by content — each name is
    ///     a true description of what the bundle holds.
    /// </summary>
    public static IReadOnlyList<string> ResolveAll(ulong key)
    {
        return Names.TryGetValue(key, out var names) ? Array.AsReadOnly(names) : NoNames;
    }

    /// <summary>
    ///     Parses a carved big-endian shell and resolves its name in one call.
    ///     Null for stub slots, unparseable buffers and unknown content — the
    ///     carver's only entry point, so a bad shell degrades to a slot name
    ///     rather than throwing during a ROM open.
    /// </summary>
    public static string? TryResolveShell(byte[] shellBytes)
    {
        var identity = TryReadShellIdentity(shellBytes);
        return identity == null ? null : TryResolve(identity.Value.Key);
    }

    /// <summary>
    ///     Reads the content key and every PS1 candidate for a carved shell.
    ///     The resolver uses the complete candidate set only when an independent
    ///     ROM filename literal can safely disambiguate it; ordinary callers
    ///     should continue to use <see cref="TryResolveShell" />.
    /// </summary>
    internal static ShellIdentity? TryReadShellIdentity(byte[] shellBytes)
    {
        PsxMeshFile? shell;
        try
        {
            shell = PsxN64ShellFile.Parse(shellBytes);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException
                                       or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }

        if (shell == null || shell.MeshNameHashes.Length == 0)
            return null;

        var key = ComputeKey(shell.MeshNameHashes);
        return key == null ? null : new ShellIdentity(key.Value, ResolveAll(key.Value));
    }

    private static FrozenDictionary<ulong, string[]> Load()
    {
        var dict = new Dictionary<ulong, string[]>();
        var assembly = typeof(N64BundleNames).Assembly;
        using var stream = assembly.GetManifestResourceStream("N64BundleNames.txt");
        if (stream == null)
            return dict.ToFrozenDictionary();

        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            // Format: KEY(16 hex)=stem[|stem...]; '#' comments.
            if (line.Length == 0 || line[0] == '#')
                continue;

            var eq = line.IndexOf('=');
            if (eq < 1 || eq >= line.Length - 1)
                continue;

            if (!ulong.TryParse(line.AsSpan(0, eq), NumberStyles.HexNumber, null, out var key))
                continue;

            dict.TryAdd(key, line[(eq + 1)..].Split('|', StringSplitOptions.RemoveEmptyEntries));
        }

        return dict.ToFrozenDictionary();
    }
}
