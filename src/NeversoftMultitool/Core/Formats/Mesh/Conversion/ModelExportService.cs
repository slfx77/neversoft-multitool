namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public static class ModelExportService
{
    public static MeshExportResult Export(ModelDocument document, MeshExportRequest request)
    {
        return request.Format switch
        {
            MeshOutputFormat.Glb => new GltfModelExporter().Export(document, request),
            MeshOutputFormat.Blend => new BlendModelExporter().Export(document, request),
            MeshOutputFormat.Both => ExportBoth(document, request),
            _ => throw new NotSupportedException($"Unsupported output format: {request.Format}")
        };
    }

    public static (byte[]? GlbBytes, int Triangles) BuildGlbBytes(ModelDocument document)
    {
        return new GltfModelExporter().BuildGlbBytes(document);
    }

    private static MeshExportResult ExportBoth(ModelDocument document, MeshExportRequest request)
    {
        var glb = new GltfModelExporter().Export(document, request);
        var blend = new BlendModelExporter().Export(document, request);
        return new MeshExportResult
        {
            OutputPaths = glb.OutputPaths.Concat(blend.OutputPaths).ToArray(),
            Triangles = Math.Max(glb.Triangles, blend.Triangles),
            MaterialCount = Math.Max(glb.MaterialCount, blend.MaterialCount),
            TextureCount = Math.Max(glb.TextureCount, blend.TextureCount),
            Warnings = glb.Warnings.Concat(blend.Warnings).ToArray()
        };
    }
}
