using System.Runtime.CompilerServices;

namespace NeversoftMultitool.Tests.Helpers;

/// <summary>
///     Marks a test that walks a full corpus or repeatedly reads real game data under
///     Sample/Builds. Explicit tests are excluded from a default run; opt in with
///     '--explicit on' (everything) or '--explicit only' (corpus tests alone).
///     A single focused fixture may stay [Fact] when it does not require a broad
///     Sample/Builds index or otherwise dominate the synthetic test suite.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CorpusFactAttribute : FactAttribute
{
    public CorpusFactAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Explicit = true;
    }
}

/// <summary>
///     Theory variant of <see cref="CorpusFactAttribute" /> for corpus sweeps and
///     multi-fixture real-file oracle suites.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CorpusTheoryAttribute : TheoryAttribute
{
    public CorpusTheoryAttribute(
        [CallerFilePath] string? sourceFilePath = null,
        [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Explicit = true;
    }
}
