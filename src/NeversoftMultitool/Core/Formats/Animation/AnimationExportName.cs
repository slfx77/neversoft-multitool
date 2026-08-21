using System.Globalization;

namespace NeversoftMultitool.Core.Formats.Animation;

/// <summary>
///     Resolves animation clip names that are written into exported models.
///     PSX animation tables contain numbered slots rather than authored names,
///     so discovery labels those slots <c>anim_N</c>. Prefix those synthetic
///     labels with the selected mesh stem to keep the exported clip meaningful.
/// </summary>
internal static class AnimationExportName
{
    public static string ForMesh(
        string meshStem,
        string animationName,
        ISet<string>? usedNames = null)
    {
        var resolvedName = animationName;
        if (!string.IsNullOrWhiteSpace(meshStem) &&
            TryGetUnnamedSlot(animationName, out var unnamedSlot))
        {
            var normalizedStem = FileNameOnly(meshStem);
            resolvedName = $"{normalizedStem}_{unnamedSlot}";
        }
        else if (TryGetNamedSlot(animationName, out var namedSlot))
        {
            // A slot the build's tricks.bin names needs no bank qualifier: the
            // trick name already identifies the clip, and "Christ Air" reads
            // better in a glTF clip list than "sk2anim.psx::Christ Air".
            resolvedName = namedSlot;
        }

        if (usedNames == null || usedNames.Add(resolvedName))
            return resolvedName;

        // Embedded and external banks can both expose anim_0. Keep the first
        // requested name clean and disambiguate subsequent clips without
        // dropping the mesh prefix.
        for (var suffix = 2; suffix < int.MaxValue; suffix++)
        {
            var candidate = $"{resolvedName}_{suffix}";
            if (usedNames.Add(candidate))
                return candidate;
        }

        throw new InvalidOperationException("Could not assign a unique animation export name.");
    }

    private static bool TryGetUnnamedSlot(string name, out string slot)
    {
        var qualifier = name.LastIndexOf("::", StringComparison.Ordinal);
        var candidate = qualifier >= 0 ? name[(qualifier + 2)..] : name;
        candidate = FileNameOnly(candidate);
        if (IsUnnamedSlot(candidate))
        {
            slot = candidate;
            return true;
        }

        slot = "";
        return false;
    }

    /// <summary>
    ///     A bank-qualified name whose slot part is a real name rather than a
    ///     synthetic <c>anim_N</c> — i.e. one <c>tricks.bin</c> supplied.
    /// </summary>
    private static bool TryGetNamedSlot(string name, out string slot)
    {
        slot = "";
        var qualifier = name.LastIndexOf("::", StringComparison.Ordinal);
        if (qualifier < 0) return false;

        var candidate = name[(qualifier + 2)..];
        if (candidate.Length == 0 || IsUnnamedSlot(FileNameOnly(candidate))) return false;

        slot = candidate;
        return true;
    }

    internal static bool IsUnnamedSlot(string name)
    {
        const string prefix = "anim_";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;

        var suffix = name[prefix.Length..];
        return suffix.Equals("x", StringComparison.OrdinalIgnoreCase)
               || (suffix.Length > 0
                   && int.TryParse(suffix, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    private static string FileNameOnly(string value)
    {
        var separator = value.LastIndexOfAny(['/', '\\']);
        return separator >= 0 ? value[(separator + 1)..] : value;
    }
}
