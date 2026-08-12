using NeversoftMultitool.Core.Formats.Mesh.Detection;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

/// <summary>
///     Preserves the Mesh Converter tab's historical output stems while making
///     archive virtual paths safe to feed into collision planning.
/// </summary>
internal static class MeshBatchOutputNaming
{
    public static string ConversionStem(string displayPath)
    {
        var normalized = NormalizeVirtualPath(displayPath);
        var stem = normalized.EndsWith(MeshTypeDetector.N64ModelSuffix, StringComparison.OrdinalIgnoreCase)
            ? MeshTypeDetector.GetN64BundleStem(normalized)
            : MeshTypeDetector.GetStem(normalized);
        return SafeStem(stem);
    }

    public static string RenderStem(string displayPath) =>
        SafeStem(MeshTypeDetector.GetStem(NormalizeVirtualPath(displayPath)));

    private static string NormalizeVirtualPath(string displayPath) => displayPath.Replace("::", "/");

    private static string SafeStem(string stem) =>
        string.IsNullOrWhiteSpace(stem) ? "mesh" : stem;
}
