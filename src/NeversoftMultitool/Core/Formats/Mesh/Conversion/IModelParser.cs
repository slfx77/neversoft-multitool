namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

public interface IModelParser
{
    ModelDocument Parse(MeshImportRequest request);
}
