namespace NeversoftMultitool;

/// <summary>
/// Child row representing a single texture within a PSX file.
/// </summary>
public sealed class PsxTextureEntry : IListEntry
{
    public bool IsChildEntry => true;

    public required string ParentFileName { get; init; }
    public required uint NameHash { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required string PaletteType { get; init; }
    public required int Index { get; init; }
    public string? ResolvedName { get; init; }

    public string HashDisplay => $"0x{NameHash:X8}";
    public string DimensionsDisplay => $"{Width}x{Height}";
    public string NameDisplay => ResolvedName ?? HashDisplay;
}
