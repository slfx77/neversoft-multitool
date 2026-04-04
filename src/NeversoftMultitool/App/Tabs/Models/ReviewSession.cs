namespace NeversoftMultitool;

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
