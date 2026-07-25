using System.Runtime.CompilerServices;

namespace NeversoftMultitool.Tests.Helpers;

/// <summary>
///     Marks a full-corpus sweep test that walks real game data under Sample/Builds
///     (hundreds to tens of thousands of files). Explicit tests are excluded from a
///     default run; opt in with '--explicit on' (everything) or '--explicit only'
///     (sweeps alone). Single-fixture tests that read one sample file stay [Fact].
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
///     Theory variant of <see cref="CorpusFactAttribute" /> for corpus sweeps that
///     enumerate per-build test cases.
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