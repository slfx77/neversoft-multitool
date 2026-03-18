namespace NeversoftMultitool.Core.Formats.Psx;

public sealed class Ps2TexResult
{
    public Ps2TexResult(List<Ps2Texture> textures)
    {
        Success = true;
        Textures = textures;
    }

    private Ps2TexResult()
    {
    }

    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public IReadOnlyList<Ps2Texture> Textures { get; init; } = [];

    public static Ps2TexResult Fail(string message)
    {
        return new Ps2TexResult { ErrorMessage = message };
    }
}
