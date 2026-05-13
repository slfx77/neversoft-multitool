namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public interface IModelExporter
{
    MeshExportResult Export(ModelDocument document, MeshExportRequest request);
}
