using System.ComponentModel;

namespace NeversoftMultitool;

/// <summary>
///     Represents a single hash being reviewed, with its candidates and metadata.
/// </summary>
public sealed class HashReviewEntry : INotifyPropertyChanged
{
    public required string HashHex { get; init; }
    public required uint HashValue { get; init; }
    public required string Type { get; init; }
    public required string[] Files { get; init; }
    public required List<HashCandidate> Candidates { get; init; }

    public string FilesDisplay => Files.Length <= 3
        ? string.Join(", ", Files)
        : $"{string.Join(", ", Files[..3])} (+{Files.Length - 3})";

#pragma warning disable CS0067 // Event is never invoked (required by INotifyPropertyChanged for WinUI bindings)
    public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067
}

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

/// <summary>
///     Session state for the hash reviewer, persisted to JSON.
/// </summary>
public sealed class ReviewSession
{
    public Dictionary<string, string> Confirmed { get; set; } = [];
    public HashSet<string> Skipped { get; set; } = [];
    public string? CurrentHash { get; set; }
    public string? BuildsDir { get; set; }
    public string? CandidatesPath { get; set; }
}
