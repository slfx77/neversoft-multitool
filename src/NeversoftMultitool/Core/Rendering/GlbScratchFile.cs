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
        var path = Path.Combine(
            Path.GetTempPath(), "NeversoftMultitool", scope,
            $"{Guid.NewGuid():N}.glb");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
}
