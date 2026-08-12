using System.Numerics;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.ArchiveFs;
using NeversoftMultitool.Core.Formats.Mesh.Conversion;
using NeversoftMultitool.Core.Formats.Mesh.N64;

namespace NeversoftMultitool.Tests.Core.Formats.Mesh.N64;

/// <summary>
///     Pins how an N64 pool's trailing four bytes are read, and how a lit one is
///     shaded (2026-08-07).
///     <para>
///         F3DEX2 reuses those bytes for a lit NORMAL or an RGBA colour and the
///         engine picks with G_LIGHTING. That bit is carried by the group
///         descriptor's <c>kind</c> bit 0x0400, ACTIVE LOW — disassembled in all
///         four ROMs, with the polarity read from the emitted G_GEOMETRYMODE
///         masks rather than assumed. It replaces a byte-magnitude heuristic
///         that admitted mid-grey (69,69,69) and light-grey (177,177,177) alike
///         and so exported 5,522 nodes of authored colour as pure white.
///     </para>
///     <para>
///         Each ROM uploads exactly one Lights1 rig at startup and never
///         rewrites it, so a lit vertex is shaded
///         <c>ambient + colour·max(0, N·L)</c> — monochrome, and bounded well
///         below white.
///     </para>
/// </summary>
public sealed class N64LightingTests(TestPaths paths)
{
    private const string Thps1N64Build = "Tony Hawk's Pro Skater (2000-2-29, N64 - Final)";
    private const string RomName = "Tony Hawk's Pro Skater (USA).z64";

    /// <summary>THPS1's measured rig: ambient 95/255, light 120/255.</summary>
    private const float Thps1Ambient = 95f / 255f;

    private const float Thps1LitCeiling = (95f + 120f) / 255f;

    private string RomPath()
    {
        var romPath = paths.FindSampleFile(Thps1N64Build, RomName);
        Assert.SkipWhen(romPath == null, "THPS1 N64 ROM sample not available");
        return romPath!;
    }

    private ModelDocument ParseBundle(string slot, out IArchiveFileSystem fs)
    {
        var romPath = RomPath();
        fs = ArchiveFileSystem.TryOpen(romPath)!;
        var backend = ArchiveAssetBackend.TryOpen(romPath)!;
        var entry = N64Bundles.FindBundle(backend, slot);
        var source = new ArchiveAssetSource(backend, entry);

        return new MeshModelParser().Parse(new MeshImportRequest
        {
            Source = source,
            FileName = entry.Name,
            OutputStem = "n64_light",
            SourceKind = ModelSourceKind.N64Model
        });
    }

    private static (float Min, float Max, float Chroma) ColourStats(ModelDocument document)
    {
        float min = 1f, max = 0f, chroma = 0f;
        foreach (var vertex in document.Meshes.SelectMany(static m => m.Primitives)
                     .SelectMany(static p => p.Vertices))
        {
            var c = vertex.Color;
            min = MathF.Min(min, MathF.Min(c.X, MathF.Min(c.Y, c.Z)));
            max = MathF.Max(max, MathF.Max(c.X, MathF.Max(c.Y, c.Z)));
            chroma = MathF.Max(chroma,
                MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z)));
        }

        return (min, max, chroma);
    }

    /// <summary>
    ///     The rig is read from the ROM rather than tabled per game, because the
    ///     two rigs in this corpus differ and a table would silently mis-shade
    ///     any build not in it.
    /// </summary>
    [CorpusFact]
    public void LightRig_IsReadOutOfTheBootImage()
    {
        using var fs = ArchiveFileSystem.TryOpen(RomPath())!;
        var backend = ArchiveAssetBackend.TryOpen(RomPath())!;
        var boot = backend.ReadEntryBytes(backend.FindByPath("boot.bin")!);

        var rig = N64LightRig.TryParse(boot);

        Assert.NotNull(rig);
        // Monochrome grey: ambient (95,95,95), light (120,120,120), dir (73,73,73).
        Assert.Equal(Thps1Ambient, rig!.Ambient.X, 3);
        Assert.Equal(rig.Ambient.X, rig.Ambient.Y);
        Assert.Equal(rig.Ambient.X, rig.Ambient.Z);
        Assert.Equal(120f / 255f, rig.Colour.X, 3);
        Assert.Equal(1f, rig.Direction.Length(), 3);
    }

    /// <summary>
    ///     A degenerate all-zero normal lands on pure ambient. That is the
    ///     hardware result for that input, not a chosen fallback — 112 groups
    ///     corpus-wide store literal <c>00 00 00 FF</c> vertices.
    /// </summary>
    [Fact]
    public void Shade_OfADegenerateNormal_IsPureAmbient()
    {
        var rig = new N64LightRig(
            new Vector3(0.25f), new Vector3(0.5f), Vector3.UnitY);

        Assert.Equal(new Vector3(0.25f), rig.Shade(Vector3.Zero));
        // And a normal facing the light reaches ambient + colour.
        Assert.Equal(new Vector3(0.75f), rig.Shade(Vector3.UnitY));
        // Facing away clamps at ambient rather than going negative.
        Assert.Equal(new Vector3(0.25f), rig.Shade(-Vector3.UnitY));
    }

    /// <summary>
    ///     Elissa's bank has the lighting bit CLEAR. Her shade must be
    ///     monochrome and inside the rig's envelope — it can never be coloured
    ///     and can never reach white, which is exactly what the old heuristic
    ///     got wrong.
    /// </summary>
    [CorpusFact]
    public void LitCharacter_IsShadedByTheRigAndNeverWhite()
    {
        var document = ParseBundle("074", out var fs);
        using var _ = fs;

        var (min, max, chroma) = ColourStats(document);
        Assert.Equal(0f, chroma, 3);
        Assert.InRange(min, Thps1Ambient - 0.002f, Thps1Ambient + 0.002f);
        Assert.InRange(max, Thps1Ambient, Thps1LitCeiling + 0.002f);
    }

    /// <summary>
    ///     THPS1's taxi is lit but stores all-zero normals, so every vertex
    ///     shades to flat ambient. Before the rig it exported black; under a
    ///     white-for-lit rule it would have exported white. Neither is right.
    /// </summary>
    [CorpusFact]
    public void LitModelWithZeroNormals_ShadesToFlatAmbient()
    {
        var document = ParseBundle("045", out var fs);
        using var _ = fs;

        var (min, max, chroma) = ColourStats(document);
        Assert.Equal(0f, chroma, 3);
        Assert.Equal(Thps1Ambient, min, 2);
        Assert.Equal(Thps1Ambient, max, 2);
    }

    /// <summary>
    ///     The reported defect. Downtown's pools have the lighting bit SET, so
    ///     they are authored COLOUR and must keep their hue — the old rule read
    ///     them as normals and exported them pure white against a level authored
    ///     at roughly 0.3 mean.
    /// </summary>
    [CorpusTheory]
    [InlineData("004")]   // Downtown, the user's report
    [InlineData("008")]   // c_kart, which the geometric oracle mis-read as lit
    public void UnlitBank_KeepsItsAuthoredColour(string bundle)
    {
        var document = ParseBundle(bundle, out var fs);
        using var _ = fs;

        var (min, _, chroma) = ColourStats(document);
        Assert.True(chroma > 0.1f, $"expected authored colour, got a monochrome model (chroma {chroma:F3})");
        Assert.True(min < Thps1Ambient, "an unlit bank should reach darker than the rig's ambient floor");
    }
}
