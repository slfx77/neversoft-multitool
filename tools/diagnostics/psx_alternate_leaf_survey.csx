#r "../../src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.dll"
#nullable enable

using System.Reflection;
using System.Numerics;
using NeversoftMultitool.Core.Formats.Mesh.Psx;
using NeversoftMultitool.Core.QbKey;

// Surveys the placement-only alternate-leaf candidates used by older PSX
// character exports against the current stitched-topology/overlap classifier.
//
// Usage from the repository root:
//   dotnet script --no-cache tools/diagnostics/psx_alternate_leaf_survey.csx -- \
//     "Sample/Builds" [--details] [--splines]
// --no-cache ensures dotnet-script reloads the freshly built project DLL.

var root = Args.Count > 0 ? Args[0] : "Sample/Builds";
var showDetails = Args.Any(static arg => arg.Equals("--details", StringComparison.OrdinalIgnoreCase));
var surveySplines = Args.Any(static arg => arg.Equals("--splines", StringComparison.OrdinalIgnoreCase));
var semanticsType = typeof(PsxMeshFile).Assembly.GetType(
    "NeversoftMultitool.Core.Formats.Mesh.Psx.PsxMeshSemantics", throwOnError: true)!;
var findAlternates = semanticsType.GetMethod(
    "FindAlternateLeafObjectIndices", BindingFlags.Static | BindingFlags.NonPublic)!;
var findMeshIndex = semanticsType.GetMethod(
    "GetCharacterMeshIndex", BindingFlags.Static | BindingFlags.NonPublic)!;
var resolverType = typeof(PsxMeshFile).Assembly.GetType(
    "NeversoftMultitool.Core.Formats.Mesh.Psx.PsxCharacterMeshResolver", throwOnError: true)!;
var resolveVertex = resolverType.GetMethod(
    "ResolveVertex", BindingFlags.Static | BindingFlags.NonPublic)!;
var splineType = typeof(PsxMeshFile).Assembly.GetType(
    "NeversoftMultitool.Core.Formats.Mesh.Conversion.PsxSplineAppendageGeometry", throwOnError: true)!;
var findControllerChains = splineType.GetMethod(
    "FindControllerChains", BindingFlags.Static | BindingFlags.NonPublic)!;

var hierarchicalFiles = 0;
var candidateFiles = 0;
var changedFiles = 0;
var splineHits = new List<(string Path, int ChainCount, int ControllerCount, string Ranges)>();
foreach (var path in Directory.EnumerateFiles(root, "*.psx", SearchOption.AllDirectories))
{
    PsxMeshFile? header;
    try
    {
        header = PsxMeshFile.ParseHeaderOnly(path);
    }
    catch
    {
        continue;
    }

    if (header is not { HasHierarchy: true, IsSuperModel: true })
        continue;
    hierarchicalFiles++;

    PsxMeshFile? file;
    try
    {
        file = PsxMeshFile.Parse(path);
    }
    catch
    {
        continue;
    }
    if (file == null)
        continue;

    if (surveySplines)
    {
        var chains = ((System.Collections.IEnumerable)findControllerChains.Invoke(null, [file])!)
            .Cast<object>()
            .ToArray();
        if (chains.Length > 0)
        {
            var objectIndicesProperty = chains[0].GetType().GetProperty("ObjectIndices")!;
            var chainIndices = chains.Select(chain =>
                    ((System.Collections.IEnumerable)objectIndicesProperty.GetValue(chain)!)
                    .Cast<object>()
                    .Select(Convert.ToInt32)
                    .ToArray())
                .ToArray();
            var controllerCount = chainIndices.Sum(static indices => indices.Length);
            var ranges = string.Join(",", chainIndices.Select(static indices => $"{indices[0]}-{indices[^1]}"));
            splineHits.Add((Path.GetRelativePath(root, path), chains.Length, controllerCount, ranges));
        }
    }

    var hasChild = new bool[file.Objects.Count];
    for (var i = 0; i < file.Objects.Count; i++)
    {
        var parent = file.Objects[i].ParentIndex;
        if (parent >= 0 && parent < hasChild.Length && parent != i)
            hasChild[parent] = true;
    }

    var oldCandidates = file.Objects
        .Select((obj, index) =>
        {
            var meshIndex = (int)findMeshIndex.Invoke(null, [file, index])!;
            return (obj, index, meshIndex);
        })
        .Where(pair => !hasChild[pair.index] && pair.obj.ParentIndex >= 0
                       && pair.meshIndex >= 0
                       && pair.meshIndex < file.Meshes.Count
                       && file.Meshes[pair.meshIndex].Faces.Count > 0)
        .GroupBy(pair => (pair.obj.ParentIndex, pair.obj.RawX, pair.obj.RawY, pair.obj.RawZ))
        .SelectMany(group => group.Skip(1).Select(pair => pair.index))
        .Order()
        .ToArray();
    if (oldCandidates.Length == 0)
        continue;
    candidateFiles++;

    var currentAlternates = ((IEnumerable<int>)findAlternates.Invoke(null, [file])!)
        .Order()
        .ToArray();
    if (oldCandidates.SequenceEqual(currentAlternates))
        continue;

    changedFiles++;
    Console.WriteLine($"{Path.GetRelativePath(root, path)}");
    Console.WriteLine($"  placement-only: [{string.Join(", ", oldCandidates)}]");
    Console.WriteLine($"  current:        [{string.Join(", ", currentAlternates)}]");
    if (showDetails)
    {
        foreach (var group in file.Objects
                     .Select((obj, index) => (obj, index))
                     .Where(pair => !hasChild[pair.index] && pair.obj.ParentIndex >= 0)
                     .GroupBy(pair => (pair.obj.ParentIndex, pair.obj.RawX, pair.obj.RawY, pair.obj.RawZ))
                     .Where(static group => group.Count() > 1))
        {
            Console.WriteLine(
                $"  group parent={group.Key.ParentIndex} pivot=({group.Key.RawX},{group.Key.RawY},{group.Key.RawZ})");
            foreach (var (_, objectIndex) in group)
                PrintObjectDetails(file, objectIndex);
        }
    }
}

Console.WriteLine(
    $"hierarchical supers={hierarchicalFiles}, placement candidates={candidateFiles}, changed={changedFiles}");

if (surveySplines)
{
    foreach (var hit in splineHits)
        Console.WriteLine(
            $"spline {hit.Path}: chains={hit.ChainCount}, controllers={hit.ControllerCount}, ranges={hit.Ranges}");
    Console.WriteLine($"spline hits={splineHits.Count}");
}

void PrintObjectDetails(PsxMeshFile file, int objectIndex)
{
    var meshIndex = (int)findMeshIndex.Invoke(null, [file, objectIndex])!;
    if (meshIndex < 0 || meshIndex >= file.Meshes.Count)
    {
        Console.WriteLine($"    object={objectIndex} mesh={meshIndex} (invalid)");
        return;
    }

    var mesh = file.Meshes[meshIndex];
    var hash = meshIndex < file.MeshNameHashes.Length ? file.MeshNameHashes[meshIndex] : 0u;
    var name = QbKey.TryResolve(hash) ?? "?";
    var stitched = mesh.Vertices.Count(static vertex => vertex.Type == 2);
    var sources = mesh.Vertices.Count(static vertex => vertex.Type == 1);
    var textured = mesh.Faces.Count(static face => face.IsTextured);
    var bounds = GetWorldBounds(file, meshIndex);
    var size = bounds.Max - bounds.Min;
    Console.WriteLine(
        $"    object={objectIndex} mesh={meshIndex} hash=0x{hash:X8} name={name} " +
        $"v/f={mesh.Vertices.Count}/{mesh.Faces.Count} stitch={stitched} source={sources} " +
        $"textured={textured} size=({size.X:F2},{size.Y:F2},{size.Z:F2}) " +
        $"bounds=({bounds.Min.X:F2},{bounds.Min.Y:F2},{bounds.Min.Z:F2}).." +
        $"({bounds.Max.X:F2},{bounds.Max.Y:F2},{bounds.Max.Z:F2})");
}

(Vector3 Min, Vector3 Max) GetWorldBounds(PsxMeshFile file, int meshIndex)
{
    var min = new Vector3(float.PositiveInfinity);
    var max = new Vector3(float.NegativeInfinity);
    for (var vertexIndex = 0; vertexIndex < file.Meshes[meshIndex].Vertices.Count; vertexIndex++)
    {
        var resolved = resolveVertex.Invoke(null, [file, meshIndex, (uint)vertexIndex])!;
        var world = (Vector3)resolved.GetType().GetProperty("WorldPosition")!.GetValue(resolved)!;
        min = Vector3.Min(min, world);
        max = Vector3.Max(max, world);
    }
    return (min, max);
}
