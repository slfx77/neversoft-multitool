using NeversoftMultitool.Core.Settings;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Finds the Blender executable used for .blend export. Resolution order:
///     an explicit per-call path (CLI <c>--blender-helper</c>), the saved user
///     setting, a helper bundled beside the app, the PATH, then well-known
///     install locations. An explicit or saved path that no longer exists fails
///     with a reason naming that path instead of silently falling through to a
///     different Blender.
/// </summary>
public static class BlenderLocator
{
    public static string? Resolve(string? explicitPath) => Resolve(explicitPath, out _);

    public static string? Resolve(string? explicitPath, out string failureReason)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var explicitExe = NormalizeExecutable(explicitPath);
            failureReason = explicitExe == null
                ? $"Blender was not found at the specified path: {explicitPath}"
                : "";
            return explicitExe;
        }

        var savedPath = UserSettings.BlenderPath;
        if (!string.IsNullOrWhiteSpace(savedPath))
        {
            var savedExe = NormalizeExecutable(savedPath);
            failureReason = savedExe == null
                ? $"The configured Blender path no longer exists: {savedPath}. " +
                  "Update it in Settings (the cog at the bottom of the navigation pane); " +
                  "the value is stored at " + UserSettings.StorageDescription + "."
                : "";
            return savedExe;
        }

        var detected = FindBundledHelper() ?? FindOnPath() ?? FindKnownInstall();
        failureReason = detected == null
            ? "Blender was not found. .blend export runs Blender in the background " +
              "to build the file. Install Blender (blender.org), then either add it " +
              "to PATH, set its location in Settings (the cog at the bottom of the " +
              "navigation pane), or pass --blender-helper <path> on the CLI."
            : "";
        return detected;
    }

    /// <summary>
    ///     Accepts an executable path or an install directory (including a macOS
    ///     .app bundle) and returns the executable inside it, or null.
    /// </summary>
    public static string? NormalizeExecutable(string path)
    {
        try
        {
            if (File.Exists(path)) return path;
            if (!Directory.Exists(path)) return null;

            string[] candidates =
            [
                Path.Combine(path, "blender.exe"),
                Path.Combine(path, "blender"),
                Path.Combine(path, "Contents", "MacOS", "Blender"),
                Path.Combine(path, "Blender.app", "Contents", "MacOS", "Blender")
            ];
            return candidates.FirstOrDefault(File.Exists);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FindBundledHelper()
    {
        var baseDir = AppContext.BaseDirectory;
        string[] candidates =
        [
            Path.Combine(baseDir, "BlenderExporter", "blender.exe"),
            Path.Combine(baseDir, "BlenderExporter", "blender", "blender.exe"),
            Path.Combine(baseDir, "BlenderExporter", "blender")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath()
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathValue)) return null;

        var exeName = OperatingSystem.IsWindows() ? "blender.exe" : "blender";
        foreach (var dir in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim('"'), exeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entry — skip it.
            }
        }

        return null;
    }

    private static string? FindKnownInstall()
    {
        return EnumerateKnownInstallCandidates().FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> EnumerateKnownInstallCandidates()
    {
        if (OperatingSystem.IsWindows())
        {
            string?[] roots =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Programs")
            ];
            foreach (var root in roots)
            {
                if (string.IsNullOrEmpty(root)) continue;
                foreach (var versionDir in EnumerateVersionedInstallDirs(
                             Path.Combine(root, "Blender Foundation")))
                {
                    yield return Path.Combine(versionDir, "blender.exe");
                }
            }

            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
            {
                yield return Path.Combine(
                    programFilesX86, "Steam", "steamapps", "common", "Blender", "blender.exe");
            }
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "/Applications/Blender.app/Contents/MacOS/Blender";
        }
        else
        {
            yield return "/usr/bin/blender";
            yield return "/usr/local/bin/blender";
            yield return "/snap/bin/blender";
        }
    }

    /// <summary>
    ///     Versioned install dirs ("Blender 4.2", "Blender 3.6"), newest first
    ///     by parsed version so a fresh side-by-side install wins.
    /// </summary>
    private static IEnumerable<string> EnumerateVersionedInstallDirs(string foundationDir)
    {
        string[] dirs;
        try
        {
            if (!Directory.Exists(foundationDir)) return [];
            dirs = Directory.GetDirectories(foundationDir, "Blender*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        return dirs.OrderByDescending(static dir =>
        {
            var suffix = Path.GetFileName(dir)["Blender".Length..].Trim();
            return Version.TryParse(suffix, out var version) ? version : new Version(0, 0);
        });
    }
}
