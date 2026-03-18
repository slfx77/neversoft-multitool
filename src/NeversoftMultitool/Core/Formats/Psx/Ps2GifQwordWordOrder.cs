namespace NeversoftMultitool.Core.Formats.Psx;

public readonly record struct Ps2GifQwordWordOrder(int Word0, int Word1, int Word2, int Word3)
{
    public static readonly Ps2GifQwordWordOrder Identity = new(0, 1, 2, 3);

    public bool IsIdentity => this == Identity;

    public int MapWord(int destinationWordIndex)
    {
        return destinationWordIndex switch
        {
            0 => Word0,
            1 => Word1,
            2 => Word2,
            3 => Word3,
            _ => throw new ArgumentOutOfRangeException(nameof(destinationWordIndex))
        };
    }

    public override string ToString()
    {
        return $"{Word0}{Word1}{Word2}{Word3}";
    }

    public static bool TryParse(string? value, out Ps2GifQwordWordOrder order)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            order = Identity;
            return true;
        }

        value = value.Trim();
        if (value.Length != 4 || value.Any(c => c is < '0' or > '3'))
        {
            order = default;
            return false;
        }

        var digits = value.Select(c => c - '0').ToArray();
        if (digits.Distinct().Count() != 4)
        {
            order = default;
            return false;
        }

        order = new Ps2GifQwordWordOrder(digits[0], digits[1], digits[2], digits[3]);
        return true;
    }
}
