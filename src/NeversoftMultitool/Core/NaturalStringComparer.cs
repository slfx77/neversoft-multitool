namespace NeversoftMultitool.Core;

/// <summary>
///     Compares display strings case-insensitively while treating consecutive
///     digits as one number. This keeps generated names such as anim_2 before
///     anim_10 without losing deterministic ordinal ordering.
/// </summary>
internal sealed class NaturalStringComparer : IComparer<string?>
{
    private NaturalStringComparer()
    {
    }

    public static NaturalStringComparer OrdinalIgnoreCase { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left == null) return -1;
        if (right == null) return 1;

        var leftIndex = 0;
        var rightIndex = 0;
        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;
                while (leftIndex < left.Length && char.IsDigit(left[leftIndex])) leftIndex++;
                while (rightIndex < right.Length && char.IsDigit(right[rightIndex])) rightIndex++;

                var leftDigits = left.AsSpan(leftStart, leftIndex - leftStart).TrimStart('0');
                var rightDigits = right.AsSpan(rightStart, rightIndex - rightStart).TrimStart('0');
                var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);
                if (lengthComparison != 0) return lengthComparison;

                var digitComparison = leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
                if (digitComparison != 0) return digitComparison;
                continue;
            }

            var characterComparison = char.ToUpperInvariant(left[leftIndex])
                .CompareTo(char.ToUpperInvariant(right[rightIndex]));
            if (characterComparison != 0) return characterComparison;
            leftIndex++;
            rightIndex++;
        }

        var totalLengthComparison = left.Length.CompareTo(right.Length);
        return totalLengthComparison != 0
            ? totalLengthComparison
            : StringComparer.OrdinalIgnoreCase.Compare(left, right);
    }
}
