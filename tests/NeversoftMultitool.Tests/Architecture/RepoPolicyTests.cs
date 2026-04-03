using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NeversoftMultitool.Tests.Architecture;

public sealed class RepoPolicyTests
{
    private const int SoftFileLineLimit = 500;

    private static readonly Dictionary<string, int> LargeFileBaseline = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void CSharpFilesStayWithinTheSoftLineLimitBaseline()
    {
        var repoRoot = FindRepositoryRoot();
        var oversizedFiles = EnumerateTrackedCSharpFiles(repoRoot)
            .Select(path => new FileLineCount(ToRepoRelativePath(repoRoot, path), CountLines(path)))
            .Where(file => file.LineCount > SoftFileLineLimit)
            .OrderByDescending(file => file.LineCount)
            .ThenBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var violations = oversizedFiles
            .Where(file => !LargeFileBaseline.TryGetValue(file.RelativePath, out var allowedLines) || file.LineCount > allowedLines)
            .ToArray();

        Assert.True(violations.Length == 0, BuildLargeFileFailureMessage(violations, oversizedFiles));
    }

    [Fact]
    public void PartialClassesStayLimitedToUiCodeBehindAndGeneratedRegexBridges()
    {
        var repoRoot = FindRepositoryRoot();
        var violations = EnumerateTrackedCSharpFiles(repoRoot)
            .Where(ContainsPartialClassDeclaration)
            .Select(path => ToRepoRelativePath(repoRoot, path))
            .Where(path => !IsAllowedPartialClassUsage(repoRoot, path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(violations.Length == 0, BuildPartialClassFailureMessage(violations));
    }

    private static IEnumerable<string> EnumerateTrackedCSharpFiles(string repoRoot)
    {
        foreach (var path in (TryEnumerateGitTrackedCSharpFiles(repoRoot) ?? EnumerateCSharpFilesFromRoots(repoRoot))
                     .Where(IsTrackedCSharpFile)
                     .Where(File.Exists))
            yield return path;
    }

    private static IEnumerable<string> EnumerateCSharpFilesFromRoots(string repoRoot)
    {
        foreach (var relativeRoot in new[] { "src", "tests" })
        {
            var absoluteRoot = Path.Combine(repoRoot, relativeRoot);
            if (!Directory.Exists(absoluteRoot))
                continue;

            foreach (var path in Directory.EnumerateFiles(absoluteRoot, "*.cs", SearchOption.AllDirectories))
                yield return path;
        }
    }

    private static string[]? TryEnumerateGitTrackedCSharpFiles(string repoRoot)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };

            process.StartInfo.ArgumentList.Add("-c");
            process.StartInfo.ArgumentList.Add("safe.directory=*");
            process.StartInfo.ArgumentList.Add("ls-files");
            process.StartInfo.ArgumentList.Add("--");
            process.StartInfo.ArgumentList.Add("src");
            process.StartInfo.ArgumentList.Add("tests");

            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            _ = process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            if (process.ExitCode != 0)
                return null;

            return output
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Where(static path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                .Select(relativePath => Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)))
                .ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static bool IsTrackedCSharpFile(string path)
    {
        return !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".designer.cs", StringComparison.OrdinalIgnoreCase) &&
               !path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsPartialClassDeclaration(string path)
    {
        if (!File.Exists(path))
            return false;

        const string partialClassPattern = @"\bpartial\s+class\b";
        return Regex.IsMatch(File.ReadAllText(path), partialClassPattern, RegexOptions.CultureInvariant);
    }

    private static bool IsAllowedPartialClassUsage(string repoRoot, string relativePath)
    {
        if (relativePath.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase))
            return true;

        var content = File.ReadAllText(Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        return content.Contains("[GeneratedRegex(", StringComparison.Ordinal);
    }

    private static int CountLines(string path)
    {
        if (!File.Exists(path))
            return 0;

        var lineCount = 0;
        foreach (var _ in File.ReadLines(path))
            lineCount++;

        return lineCount;
    }

    private static string ToRepoRelativePath(string repoRoot, string path)
    {
        return Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Directory.Build.props")))
                return current.FullName;

            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory.");
    }

    private static string BuildLargeFileFailureMessage(
        FileLineCount[] violations,
        FileLineCount[] oversizedFiles)
    {
        var builder = new StringBuilder();
        builder.Append("Tracked C# files under src/ and tests/ should stay at or below ");
        builder.Append(SoftFileLineLimit.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(" lines unless explicitly tracked in the repo-policy baseline.");
        builder.AppendLine("Split new oversized files instead of extending the baseline.");
        builder.AppendLine();

        if (violations.Length > 0)
        {
            builder.AppendLine("Violations:");
            foreach (var violation in violations)
            {
                builder.Append("  - ");
                builder.Append(violation.RelativePath);
                builder.Append(" (");
                builder.Append(violation.LineCount.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine(" lines)");
            }

            builder.AppendLine();
        }

        builder.AppendLine("Current baseline exceptions:");
        foreach (var file in oversizedFiles)
        {
            builder.Append("  - ");
            builder.Append(file.RelativePath);
            builder.Append(" (");
            builder.Append(file.LineCount.ToString(CultureInfo.InvariantCulture));
            builder.AppendLine(" lines)");
        }

        return builder.ToString();
    }

    private static string BuildPartialClassFailureMessage(string[] violations)
    {
        var builder = new StringBuilder();
        builder.AppendLine("partial class usage is reserved for XAML code-behind and GeneratedRegex bridge types.");
        builder.AppendLine("Prefer single-definition classes elsewhere.");

        if (violations.Length > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Violations:");
            foreach (var violation in violations)
            {
                builder.Append("  - ");
                builder.AppendLine(violation);
            }
        }

        return builder.ToString();
    }

    private sealed record FileLineCount(string RelativePath, int LineCount);
}
