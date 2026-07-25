using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Conversion;

internal readonly record struct PsxSplineSample(
    Vector3 Center,
    ModelBoneInfluences Influence);
