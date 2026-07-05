using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Skin;
using NeversoftMultitool.Tests.Helpers;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.Ps2Scene.Skin;

/// <summary>
///     Proving Ground re-encodes .skin.ps2 positions as Q4.12 (wrapping mod 16 units);
///     the same character ships in Project 8 with plain Q12.4 positions, giving an
///     exact per-vertex oracle for the band reconstruction in ThpgPositionUnwrapper.
/// </summary>
public sealed class ThpgQ412UnwrapTests(TestPaths paths)
{
    private const string P8Build = "Tony Hawk's Project 8 (2006-9-21, PS2 - Final)";
    private const string ThpgBuild = "Tony Hawk's Proving Ground (2007-9-3, PS2 - Final)";

    [Fact]
    public void ThpgGpedBam_UnwrapsToMatchP8Oracle()
    {
        Assert.SkipWhen(!paths.HasSampleBuilds, "Sample builds not available");
        var p8Path = paths.FindSampleFile(P8Build, "gped_bam.skin.ps2");
        var thpgPath = paths.FindSampleFile(ThpgBuild, "gped_bam.skin.ps2");
        Assert.SkipWhen(p8Path is null, "P8 gped_bam.skin.ps2 not found");
        Assert.SkipWhen(thpgPath is null, "THPG gped_bam.skin.ps2 not found");

        var p8 = ThawPs2SkinFile.Parse(File.ReadAllBytes(p8Path!));
        var thpg = ThawPs2SkinFile.Parse(File.ReadAllBytes(thpgPath!));

        var p8Meshes = p8.MeshGroups.SelectMany(static g => g.Meshes).ToList();
        var thpgMeshes = thpg.MeshGroups.SelectMany(static g => g.Meshes).ToList();
        Assert.Equal(p8Meshes.Count, thpgMeshes.Count);

        var total = 0;
        var mismatched = 0;
        var bandErrors = new Dictionary<(int X, int Y, int Z), int>();
        var report = new System.Text.StringBuilder();
        for (var m = 0; m < p8Meshes.Count; m++)
        {
            var a = p8Meshes[m].Vertices;
            var b = thpgMeshes[m].Vertices;
            Assert.Equal(a.Length, b.Length);
            var meshMismatch = 0;
            for (var i = 0; i < a.Length; i++)
            {
                total++;
                var d = b[i].Position - a[i].Position;
                // P8 quantizes to 1/16; THPG to 1/4096 — matched positions differ < 1/16.
                if (MathF.Abs(d.X) < 0.1f && MathF.Abs(d.Y) < 0.1f && MathF.Abs(d.Z) < 0.1f)
                    continue;

                mismatched++;
                meshMismatch++;
                var key = (
                    (int)MathF.Round(d.X / 16f),
                    (int)MathF.Round(d.Y / 16f),
                    (int)MathF.Round(d.Z / 16f));
                bandErrors.TryGetValue(key, out var n);
                bandErrors[key] = n + 1;
            }

            if (meshMismatch > 0)
            {
                var minY = a.Min(static v => v.Position.Y);
                var maxY = a.Max(static v => v.Position.Y);
                var minX = a.Min(static v => v.Position.X);
                var maxX = a.Max(static v => v.Position.X);
                report.AppendLine(
                    $"mesh {m}: {meshMismatch}/{a.Length} mismatched " +
                    $"(material {p8Meshes[m].MaterialChecksum:X8}, true x[{minX:F1},{maxX:F1}] y[{minY:F1},{maxY:F1}])");
            }
        }

        if (mismatched > 0)
        {
            report.AppendLine($"TOTAL: {mismatched}/{total} mismatched");
            report.AppendLine("band-error histogram (dx,dy,dz in 16-unit bands -> count):");
            foreach (var (key, n) in bandErrors.OrderByDescending(static kv => kv.Value).Take(12))
                report.AppendLine($"  {key}: {n}");
        }

        // Known limitation: a handful of small boundary/detail pieces (~8% of vertices
        // on gped_bam) still resolve to a neighbouring 16-unit band — section labels of
        // boundary kicks are unreliable and pure-proximity placement is ambiguous for
        // tiny parts. The threshold guards the reconstruction from regressing; the
        // remaining gap is tracked in docs/backlog/game-thpg-p8.md.
        var matchRate = total == 0 ? 0.0 : (total - mismatched) / (double)total;
        Assert.True(matchRate >= 0.90, $"match rate {matchRate:P1} below 90%\n{report}");
    }
}
