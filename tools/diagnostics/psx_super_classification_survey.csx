#r "../../src/NeversoftMultitool/bin/Release/net10.0/NeversoftMultitool.dll"
#nullable enable

using NeversoftMultitool.Core.Formats.Mesh.Psx;

// Audits the former `(HIER || anim) && objectCount <= 32` super heuristic
// against the runtime-derived rule in which only a 0x2A/0x2C animation chunk
// sets IsSuper. In particular, this identifies small HIER-only files that the
// old heuristic scaled as character supers and reports independent character
// evidence (stitched vertices and object Super flags).
//
// Usage from the repository root:
//   dotnet script --no-cache tools/diagnostics/psx_super_classification_survey.csx -- \
//     "Sample/Builds"

var root = Args.Count > 0 ? Args[0] : "Sample/Builds";
var psxCount = 0;
var hierOnlyCount = 0;
var oldSuperNowItemCount = 0;
var plausibleCharacterCount = 0;
var promotedLargeAnimCount = 0;

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

    if (header == null)
        continue;
    psxCount++;

    if (header.IsSuperModel && header.Objects.Count > 32)
    {
        promotedLargeAnimCount++;
        Console.WriteLine(
            $"large-anim-super {Path.GetRelativePath(root, path)}: " +
            $"v{header.Version} objects={header.Objects.Count} meshes={header.MeshNameHashes.Length}");
    }

    if (!header.HasHierarchy || header.IsSuperModel)
        continue;
    hierOnlyCount++;

    // This is the exact demotion set: large HIER-only files were excluded by
    // the former 32-part cap too and therefore did not change classification.
    if (header.Objects.Count > 32)
    {
        Console.WriteLine(
            $"unchanged-large-hier-only {Path.GetRelativePath(root, path)}: " +
            $"v{header.Version} objects={header.Objects.Count} meshes={header.MeshNameHashes.Length}");
        continue;
    }
    oldSuperNowItemCount++;

    PsxMeshFile? file;
    try
    {
        file = PsxMeshFile.Parse(path);
    }
    catch
    {
        file = null;
    }

    var stitched = file?.HasStitchedReferences ?? false;
    var superFlagObjects = header.Objects.Count(static obj => obj.IsCharacter);
    var plausibleCharacter = stitched || superFlagObjects > 0;
    if (plausibleCharacter)
        plausibleCharacterCount++;

    Console.WriteLine(
        $"demoted-hier-only {Path.GetRelativePath(root, path)}: " +
        $"v{header.Version} objects={header.Objects.Count} meshes={header.MeshNameHashes.Length} " +
        $"stitched={stitched} superFlags={superFlagObjects} plausibleCharacter={plausibleCharacter} " +
        $"scale={header.ScaleDivisor:F2}");
}

Console.WriteLine(
    $"parsed={psxCount}, HIER-without-anim={hierOnlyCount}, " +
    $"old-super-now-item={oldSuperNowItemCount}, " +
    $"plausible-character-demotions={plausibleCharacterCount}, " +
    $"large-anim-supers={promotedLargeAnimCount}");
