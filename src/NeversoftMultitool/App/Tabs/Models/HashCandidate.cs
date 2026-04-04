namespace NeversoftMultitool;

/// <summary>
///     A single brute-force candidate for a hash.
/// </summary>
public sealed class HashCandidate
{
    public required string Name { get; init; }
    public required int Score { get; init; }
    public required int Length { get; init; }

    public string Display => $"{Name}  (score: {Score}, len: {Length})";
}
