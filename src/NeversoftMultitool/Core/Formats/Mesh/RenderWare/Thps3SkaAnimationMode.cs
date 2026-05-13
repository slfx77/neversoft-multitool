namespace NeversoftMultitool.Core.Formats.Mesh.RenderWare;

internal readonly record struct Thps3SkaAnimationMode(
    Thps3SkaRotationMode RotationMode,
    Thps3SkaTranslationMode TranslationMode)
{
    public static Thps3SkaAnimationMode Default { get; } =
        new(Thps3SkaRotationMode.BindRaw, Thps3SkaTranslationMode.Anchored);

    public string Name => (RotationMode, TranslationMode) switch
    {
        (Thps3SkaRotationMode.BindRaw, Thps3SkaTranslationMode.Anchored) => "bind-raw",
        (Thps3SkaRotationMode.DirectRaw, Thps3SkaTranslationMode.Anchored) => "direct-raw",
        (Thps3SkaRotationMode.BindConjugated, Thps3SkaTranslationMode.Anchored) => "bind-conjugated",
        (Thps3SkaRotationMode.DirectConjugated, Thps3SkaTranslationMode.Anchored) => "direct-conjugated",
        (Thps3SkaRotationMode.BindRaw, Thps3SkaTranslationMode.Raw) => "bind-raw-rawt",
        (Thps3SkaRotationMode.DirectRaw, Thps3SkaTranslationMode.Raw) => "direct-raw-rawt",
        _ => $"{RotationMode.ToString().ToLowerInvariant()}-{TranslationMode.ToString().ToLowerInvariant()}"
    };

    public static IReadOnlyList<Thps3SkaAnimationMode> KnownModes { get; } =
    [
        Default,
        new(Thps3SkaRotationMode.DirectRaw, Thps3SkaTranslationMode.Anchored),
        new(Thps3SkaRotationMode.BindConjugated, Thps3SkaTranslationMode.Anchored),
        new(Thps3SkaRotationMode.DirectConjugated, Thps3SkaTranslationMode.Anchored),
        new(Thps3SkaRotationMode.BindRaw, Thps3SkaTranslationMode.Raw),
        new(Thps3SkaRotationMode.DirectRaw, Thps3SkaTranslationMode.Raw)
    ];

    public static string KnownModeNames => string.Join(", ", KnownModes.Select(static mode => mode.Name));

    public static bool TryParse(string? value, out Thps3SkaAnimationMode mode, out string error)
    {
        mode = Default;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
            return true;

        var normalized = value.Trim().ToLowerInvariant();
        var matched = KnownModes
            .Where(candidate => candidate.Name.Equals(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(static candidate => (Thps3SkaAnimationMode?)candidate)
            .FirstOrDefault();
        if (matched.HasValue)
        {
            mode = matched.Value;
            return true;
        }

        error = $"Unknown THPS3 mode '{value}'. Expected one of: {KnownModeNames}.";
        return false;
    }
}
