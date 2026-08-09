namespace SampleGenerator;

internal static class SampleGeneratorSelfTests
{
    internal static int Run()
    {
        var root = Directory.CreateTempSubdirectory("SampleGenerator_PathSafety_Root_").FullName;
        var outside = Directory.CreateTempSubdirectory("SampleGenerator_PathSafety_Outside_").FullName;
        var link = Path.Combine(root, "link");
        var checks = 0;

        try
        {
            var nestedRelative = Path.Combine("nested", "payload.bin");
            var expected = Path.GetFullPath(Path.Combine(root, nestedRelative));
            var actual = SampleGeneratorPathSafety.ResolveDestinationPath(root, nestedRelative);
            Assert(actual == expected, "normal nested output path");
            checks++;

            AssertRejected(Path.Combine("..", "escape.bin"), root, "parent traversal");
            AssertRejected(Path.GetFullPath(Path.Combine(root, "..", "absolute.bin")), root, "rooted path");
            AssertRejected(".", root, "destination root alias");
            checks += 3;

            var nonexistentRoot = Path.Combine(root, "not-created");
            var nonexistentTarget = SampleGeneratorPathSafety.ResolveDestinationPath(
                nonexistentRoot,
                "payload.bin");
            Assert(nonexistentTarget == Path.GetFullPath(Path.Combine(nonexistentRoot, "payload.bin")),
                "nonexistent destination root");
            checks++;

            Assert(SampleGeneratorPathSafety.SanitizeDiscPathSegment(".").Length == 0,
                "single-dot disc entry");
            Assert(SampleGeneratorPathSafety.SanitizeDiscPathSegment("..").Length == 0,
                "double-dot disc entry");
            Assert(SampleGeneratorPathSafety.SanitizeDiscPathSegment("dir/name") == "dir_name",
                "embedded separator");
            checks += 3;

            var linkCreated = false;
            try
            {
                Directory.CreateSymbolicLink(link, outside);
                linkCreated = true;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
            {
                Console.WriteLine("Reparse-point check skipped: symbolic links are unavailable.");
            }

            if (linkCreated)
            {
                AssertRejected(Path.Combine("link", "escape.bin"), root, "reparse traversal");
                checks++;
            }

            Console.WriteLine($"SampleGenerator path-safety self-test passed ({checks} checks).");
            return 0;
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
            Directory.Delete(root, recursive: true);
            Directory.Delete(outside, recursive: true);
        }
    }

    private static void AssertRejected(string relativePath, string root, string description)
    {
        try
        {
            SampleGeneratorPathSafety.ResolveDestinationPath(root, relativePath);
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            return;
        }

        throw new InvalidOperationException($"Path-safety self-test failed: {description} was accepted.");
    }

    private static void Assert(bool condition, string description)
    {
        if (!condition)
            throw new InvalidOperationException($"Path-safety self-test failed: {description}.");
    }
}
