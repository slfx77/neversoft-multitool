namespace NeversoftMultitool.Core.Rendering;

/// <summary>
///     Scratch .glb files for renderers that need a real path
///     (<see cref="GlbRenderer" /> / <see cref="GlbGifRenderer" /> load from
///     disk). Unique names so concurrent renders never collide.
/// </summary>
public static class GlbScratchFile
{
    /// <summary>Write GLB bytes to a unique temp path under the given scope.</summary>
    public static string Write(byte[] glbBytes, string scope)
    {
        var scopeDirectory = ResolveScopeDirectory(scope);
        var path = Path.Combine(scopeDirectory, $"{Guid.NewGuid():N}.glb");
        Directory.CreateDirectory(scopeDirectory);
        File.WriteAllBytes(path, glbBytes);
        return path;
    }

    public static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            /* ignore */
        }
    }

    private static string ResolveScopeDirectory(string scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        const string invalidScopeMessage =
            "GLB scratch scope must stay within the NeversoftMultitool temporary directory.";

        string scratchRoot;
        string scopeDirectory;
        try
        {
            scratchRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "NeversoftMultitool"));
            scopeDirectory = Path.GetFullPath(Path.Combine(scratchRoot, scope));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new ArgumentException(invalidScopeMessage, nameof(scope), exception);
        }

        var relativeScope = Path.GetRelativePath(scratchRoot, scopeDirectory);
        if (Path.IsPathRooted(relativeScope)
            || relativeScope.Equals("..", StringComparison.Ordinal)
            || relativeScope.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            || relativeScope.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(invalidScopeMessage, nameof(scope));
        }

        return scopeDirectory;
    }
}
