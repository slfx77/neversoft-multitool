using System.Globalization;
using System.Text;
using System.Text.Json;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal static partial class GsDumpAuditRunner
{
    public static GsDumpAuditReport Run(
        string gsPath,
        string outputDirectory,
        GsDumpAuditOptions options)
    {
        Directory.CreateDirectory(outputDirectory);

        var dump = GsDumpFile.ParseFile(gsPath);
        var pngPath = ResolvePngPath(gsPath, options.PngPath);
        var dimensions = ResolveRenderDimensions(dump, pngPath);
        var coordinateScale = ResolveCoordinateScale(dump);
        var textureContext = BuildTextureContext(options.TexturePath);
        var stem = MakeOutputStem(gsPath);
        var textureDumpDirectory = Path.Combine(outputDirectory, stem + ".textures");
        var textureDumpIndex = 0;
        StreamWriter? probeWriter = null;
        try
        {
            if (options.ProbeX.HasValue || options.ProbeY.HasValue || options.ProbeFbp.HasValue)
            {
                var defaultStem = options.ProbeFbp.HasValue
                    ? $"{stem}.probe-fbp{options.ProbeFbp.Value}-x{options.ProbeX?.ToString() ?? "any"}-y{options.ProbeY?.ToString() ?? "any"}.csv"
                    : $"{stem}.probe-{options.ProbeX!.Value}-{options.ProbeY!.Value}.csv";
                var probePath = options.ProbeOutputPath
                                ?? Path.Combine(outputDirectory, defaultStem);
                Directory.CreateDirectory(Path.GetDirectoryName(probePath)!);
                probeWriter = new StreamWriter(probePath, false);
                probeWriter.WriteLine(
                    "draw,primitive,fbp,fbw,psm,fbmsk,tex0,alpha,test,z,tme,abe,sampled," +
                    "sample_r,sample_g,sample_b,sample_a," +
                    "vertex_r,vertex_g,vertex_b,vertex_a," +
                    "src_r,src_g,src_b,src_a," +
                    "has_dst,dst_r,dst_g,dst_b,dst_a," +
                    "written_r,written_g,written_b,written_a");
            }

            Action<GsPixelProbeInfo>? probeSink = probeWriter == null
                ? null
                : info =>
                {
                    probeWriter.WriteLine(string.Join(',', new[]
                    {
                        info.DrawIndex.ToString(CultureInfo.InvariantCulture),
                        info.Primitive,
                        info.Fbp.ToString(CultureInfo.InvariantCulture),
                        info.Fbw.ToString(CultureInfo.InvariantCulture),
                        $"0x{info.Psm:X2}",
                        $"0x{info.Fbmsk:X8}",
                        $"0x{info.Tex0:X16}",
                        $"0x{info.AlphaRegister:X16}",
                        $"0x{info.TestRegister:X16}",
                        info.Z.ToString("F2", CultureInfo.InvariantCulture),
                        info.TextureEnabled ? "1" : "0",
                        info.BlendEnabled ? "1" : "0",
                        info.TextureSampled ? "1" : "0",
                        info.SampleR.ToString("F2", CultureInfo.InvariantCulture),
                        info.SampleG.ToString("F2", CultureInfo.InvariantCulture),
                        info.SampleB.ToString("F2", CultureInfo.InvariantCulture),
                        info.SampleA.ToString("F2", CultureInfo.InvariantCulture),
                        info.VertexR.ToString("F2", CultureInfo.InvariantCulture),
                        info.VertexG.ToString("F2", CultureInfo.InvariantCulture),
                        info.VertexB.ToString("F2", CultureInfo.InvariantCulture),
                        info.VertexA.ToString("F2", CultureInfo.InvariantCulture),
                        info.SrcR.ToString("F2", CultureInfo.InvariantCulture),
                        info.SrcG.ToString("F2", CultureInfo.InvariantCulture),
                        info.SrcB.ToString("F2", CultureInfo.InvariantCulture),
                        info.SrcA.ToString("F2", CultureInfo.InvariantCulture),
                        info.HasPreBlendDst ? "1" : "0",
                        info.PreBlendDstR.ToString(CultureInfo.InvariantCulture),
                        info.PreBlendDstG.ToString(CultureInfo.InvariantCulture),
                        info.PreBlendDstB.ToString(CultureInfo.InvariantCulture),
                        info.PreBlendDstA.ToString(CultureInfo.InvariantCulture),
                        info.WrittenR.ToString(CultureInfo.InvariantCulture),
                        info.WrittenG.ToString(CultureInfo.InvariantCulture),
                        info.WrittenB.ToString(CultureInfo.InvariantCulture),
                        info.WrittenA.ToString(CultureInfo.InvariantCulture)
                    }));
                };

            var interpretation = GsGifInterpreter.Interpret(
                dump,
                new GsGifInterpretOptions
                {
                    Width = dimensions.Width,
                    Height = dimensions.Height,
                    CoordinateScaleX = coordinateScale.X,
                    CoordinateScaleY = coordinateScale.Y,
                    TextureResolver = textureContext == null ? null : textureContext.ResolveTexture,
                    ProbeX = options.ProbeX,
                    ProbeY = options.ProbeY,
                    ProbeFbp = options.ProbeFbp,
                    PixelProbe = probeSink,
                    MaxVsync = options.MaxVsync,
                    SaveRtStart = options.SaveRtStart,
                    SaveRtCount = options.SaveRtCount,
                    SaveRtFbp = options.SaveRtFbp,
                    SaveRtOnStateTransition = options.SaveRtOnStateTransition,
                    SaveRtSink = options.SaveRtDir == null
                        ? null
                        : snapshot =>
                        {
                            var saveDir = options.SaveRtDir;
                            Directory.CreateDirectory(saveDir);
                            var fileName =
                                $"{snapshot.DrawIndex:D5}_rt_{snapshot.Fbp:X5}_{(snapshot.Psm == Ps2TexPixelDecoder.PSMCT16 || snapshot.Psm == Ps2GsVram.PSMCT16S ? "C_16" : snapshot.Psm == Ps2TexPixelDecoder.PSMCT24 ? "C_24" : "C_32")}.png";
                            var path = Path.Combine(saveDir, fileName);
                            SaveRgba(path, snapshot.Rgba, snapshot.Width, snapshot.Height);
                        },
                    DumpVramRegions = options.DumpVramRegions,
                    DumpVramRegionSink = options.DumpVramRegions == null
                        ? null
                        : (tbp, fbw, psm, w, h, rgba) =>
                        {
                            var saveDir = options.SaveRtDir ?? Path.Combine(outputDirectory, $"{stem}.vram_regions");
                            Directory.CreateDirectory(saveDir);
                            var psmTag = psm switch
                            {
                                Ps2TexPixelDecoder.PSMCT32 => "C_32",
                                Ps2TexPixelDecoder.PSMCT24 => "C_24",
                                Ps2TexPixelDecoder.PSMCT16 => "C_16",
                                Ps2GsVram.PSMCT16S => "C_16S",
                                Ps2GsVram.PSMZ32 => "Z_32",
                                Ps2GsVram.PSMZ24 => "Z_24",
                                _ => $"X_{psm:X2}"
                            };
                            var fileName = $"vram_tbp{tbp:X5}_fbw{fbw}_{psmTag}_{w}x{h}.png";
                            var path = Path.Combine(saveDir, fileName);
                            SaveRgba(path, rgba, w, h);
                        },
                    TextureDumpSink = options.JsonOnly
                        ? null
                        : dumpTexture =>
                        {
                            Directory.CreateDirectory(textureDumpDirectory);
                            var fileName =
                                $"{textureDumpIndex++:D4}_tex0-{dumpTexture.Audit.Tex0[2..]}_texa-{dumpTexture.Audit.Texa[2..]}_psm-{dumpTexture.Audit.Psm:X2}_{dumpTexture.Audit.Width}x{dumpTexture.Audit.Height}_at-{dumpTexture.Audit.RegionX}-{dumpTexture.Audit.RegionY}_{MakeSafeFileSuffix(dumpTexture.Audit.Source)}_{dumpTexture.Audit.ContentHash:X8}.png";
                            var path = Path.Combine(textureDumpDirectory, fileName);
                            SaveRgba(path, dumpTexture.Rgba, dumpTexture.Audit.Width, dumpTexture.Audit.Height);
                            return Path.GetFullPath(path);
                        }
                });
            return FinishRun(interpretation, dump, gsPath, outputDirectory, options, dimensions, pngPath, stem,
                textureContext, textureDumpDirectory);
        }
        finally
        {
            probeWriter?.Dispose();
        }
    }

    private static GsDumpAuditReport FinishRun(
        GsGifInterpretation interpretation,
        GsDumpFile dump,
        string gsPath,
        string outputDirectory,
        GsDumpAuditOptions options,
        (int Width, int Height) dimensions,
        string? pngPath,
        string stem,
        GsTextureContext? textureContext,
        string textureDumpDirectory)
    {
        var directPixels = interpretation.DirectPixels;
        var renderPixels = interpretation.Pixels;
        var rawDirectBounds = BuildPixelBounds(directPixels, dimensions.Width, dimensions.Height);
        var rawRenderBounds = BuildPixelBounds(renderPixels, dimensions.Width, dimensions.Height);
        interpretation.Render.RawDirectNonBlackBounds = rawDirectBounds;
        interpretation.Render.RawPresentedNonBlackBounds = rawRenderBounds;

        var reference = LoadReferencePixels(dump, pngPath);
        if (reference != null &&
            reference.Width == dimensions.Width &&
            reference.Height == dimensions.Height)
        {
            var referenceBounds = BuildPixelBounds(reference.Pixels, reference.Width, reference.Height);
            if (TryFitToReferencePresentation(
                    renderPixels,
                    dimensions.Width,
                    dimensions.Height,
                    rawRenderBounds,
                    referenceBounds,
                    out var fittedRenderPixels))
            {
                renderPixels = fittedRenderPixels;
                if (TryFitToReferencePresentation(
                        directPixels,
                        dimensions.Width,
                        dimensions.Height,
                        rawDirectBounds,
                        referenceBounds,
                        out var fittedDirectPixels))
                {
                    directPixels = fittedDirectPixels;
                }

                interpretation.Render.PresentationFitApplied = true;
                interpretation.Render.PresentationFitReason =
                    "reference_nonblack_bounds_fit";
                interpretation.Render.PresentationSourceBounds = rawRenderBounds;
                interpretation.Render.PresentationReferenceBounds = referenceBounds;
                AddCount(interpretation.Render.Approximations, "presentation_fit_from_reference_bounds");
            }
        }

        interpretation.Render.DirectNonBlackBounds =
            BuildPixelBounds(directPixels, dimensions.Width, dimensions.Height);
        interpretation.Render.PresentedNonBlackBounds =
            BuildPixelBounds(renderPixels, dimensions.Width, dimensions.Height);

        var rawDirectRenderPath = Path.Combine(outputDirectory, stem + ".raw-direct.png");
        var rawRenderPath = Path.Combine(outputDirectory, stem + ".raw-render.png");
        var directRenderPath = Path.Combine(outputDirectory, stem + ".direct.png");
        var renderPath = Path.Combine(outputDirectory, stem + ".render.png");
        var directDiffPath = Path.Combine(outputDirectory, stem + ".direct.diff.png");
        var diffPath = Path.Combine(outputDirectory, stem + ".diff.png");
        var materialDumpPath = Path.Combine(outputDirectory, stem + ".materials.csv");
        var jsonPath = Path.Combine(outputDirectory, stem + ".gsdump-audit.json");
        var embeddedScreenshotPath = HasEmbeddedScreenshot(dump)
            ? Path.Combine(outputDirectory, stem + ".pcsx2.png")
            : null;

        if (!options.JsonOnly)
        {
            if (embeddedScreenshotPath != null)
                SaveRgba(embeddedScreenshotPath, ConvertEmbeddedScreenshotToRgba(dump), dump.ScreenshotWidth,
                    dump.ScreenshotHeight);
            SaveRgba(rawDirectRenderPath, interpretation.DirectPixels, dimensions.Width, dimensions.Height);
            SaveRgba(rawRenderPath, interpretation.Pixels, dimensions.Width, dimensions.Height);
            SaveRgba(directRenderPath, directPixels, dimensions.Width, dimensions.Height);
            SaveRgba(renderPath, renderPixels, dimensions.Width, dimensions.Height);

            var snapshotPaths = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var snapshot in interpretation.FramebufferSnapshots)
            {
                var snapshotPath = Path.Combine(outputDirectory, $"{stem}.{MakeSafeFileSuffix(snapshot.Key)}.png");
                SaveRgba(snapshotPath, snapshot.Rgba, snapshot.Width, snapshot.Height);
                snapshotPaths[snapshot.Key] = Path.GetFullPath(snapshotPath);
            }

            foreach (var snapshotAudit in interpretation.Render.FramebufferSnapshots)
            {
                if (snapshotPaths.TryGetValue(snapshotAudit.Key, out var snapshotPath))
                    snapshotAudit.Path = snapshotPath;
            }

            SaveMaterialCsv(materialDumpPath, interpretation.Render.Materials);
        }

        GsPixelDiffStats? directDiffStats = null;
        GsPixelDiffStats? diffStats = null;
        if (HasEmbeddedScreenshot(dump))
        {
            directDiffStats = CompareAgainstPixels(
                directPixels,
                dimensions.Width,
                dimensions.Height,
                ConvertEmbeddedScreenshotToRgba(dump),
                dump.ScreenshotWidth,
                dump.ScreenshotHeight,
                options.JsonOnly ? null : directDiffPath);
            diffStats = CompareAgainstPixels(
                renderPixels,
                dimensions.Width,
                dimensions.Height,
                ConvertEmbeddedScreenshotToRgba(dump),
                dump.ScreenshotWidth,
                dump.ScreenshotHeight,
                options.JsonOnly ? null : diffPath);
        }
        else if (pngPath != null)
        {
            directDiffStats = CompareAgainstPng(
                directPixels,
                dimensions.Width,
                dimensions.Height,
                pngPath,
                options.JsonOnly ? null : directDiffPath);
            diffStats = CompareAgainstPng(
                renderPixels,
                dimensions.Width,
                dimensions.Height,
                pngPath,
                options.JsonOnly ? null : diffPath);
        }

        var report = new GsDumpAuditReport
        {
            InputPath = Path.GetFullPath(gsPath),
            ScreenshotPath = pngPath == null ? null : Path.GetFullPath(pngPath),
            EmbeddedScreenshotPath = embeddedScreenshotPath == null ? null : Path.GetFullPath(embeddedScreenshotPath),
            RawDirectRenderPath = options.JsonOnly ? null : Path.GetFullPath(rawDirectRenderPath),
            RawRenderPath = options.JsonOnly ? null : Path.GetFullPath(rawRenderPath),
            DirectRenderPath = options.JsonOnly ? null : Path.GetFullPath(directRenderPath),
            RenderPath = options.JsonOnly ? null : Path.GetFullPath(renderPath),
            DirectDiffPath = options.JsonOnly || directDiffStats == null ? null : Path.GetFullPath(directDiffPath),
            DiffPath = options.JsonOnly || diffStats == null ? null : Path.GetFullPath(diffPath),
            MaterialDumpPath = options.JsonOnly ? null : Path.GetFullPath(materialDumpPath),
            TextureDumpDirectory = options.JsonOnly ? null : Path.GetFullPath(textureDumpDirectory),
            Crc = dump.Crc,
            Serial = dump.Serial,
            StateVersion = dump.StateVersion,
            StateSize = dump.State.Length,
            RegisterSnapshotSize = dump.Registers.Length,
            ScreenshotWidth = dump.ScreenshotWidth,
            ScreenshotHeight = dump.ScreenshotHeight,
            PacketCount = dump.Packets.Count,
            PacketTypeCounts = BuildPacketTypeCounts(dump),
            TransferStats = BuildTransferStats(dump),
            Gif = interpretation.Gif,
            Render = interpretation.Render,
            DirectPixelDiff = directDiffStats,
            PixelDiff = diffStats,
            TextureCorrelation = textureContext?.BuildCorrelation(interpretation.XyzByTex0)
        };

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(
            report,
            GsDumpAuditJsonContext.Default.GsDumpAuditReport));
        return report;
    }

    private static string MakeOutputStem(string path)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        foreach (var c in Path.GetInvalidFileNameChars())
            stem = stem.Replace(c, '_');
        return stem;
    }

    private static string MakeSafeFileSuffix(string value)
    {
        var suffix = value
            .Replace(',', '_')
            .Replace('=', '-')
            .Replace("0x", "", StringComparison.OrdinalIgnoreCase);
        foreach (var c in Path.GetInvalidFileNameChars())
            suffix = suffix.Replace(c, '_');
        return suffix;
    }

    private sealed class GsTextureContext(
        MeshChecksumTextureResolver textureResolver,
        Func<ulong, uint, Ps2GeomTextureResolution> tex0Resolver)
    {
        private readonly Dictionary<ulong, GsResolvedTexture?> cache = [];

        public GsResolvedTexture? ResolveTexture(ulong tex0)
        {
            if (cache.TryGetValue(tex0, out var cached))
                return cached;

            var resolution = tex0Resolver(tex0, 0);
            if (resolution.Checksum == 0)
            {
                cache[tex0] = null;
                return null;
            }

            var pngBytes = textureResolver(resolution.Checksum);
            if (pngBytes == null)
            {
                cache[tex0] = null;
                return null;
            }

            using var image = Image.Load<Rgba32>(pngBytes);
            var rgba = new byte[image.Width * image.Height * 4];
            image.CopyPixelDataTo(rgba);
            var texture = new GsResolvedTexture(image.Width, image.Height, rgba, resolution.Checksum);
            cache[tex0] = texture;
            return texture;
        }

        public GsTextureCorrelationAudit BuildCorrelation(Dictionary<ulong, long> xyzByTex0)
        {
            var rows = new List<GsTex0CorrelationRow>();
            var resolved = 0;
            foreach (var (tex0, xyz) in xyzByTex0.OrderByDescending(static kv => kv.Value))
            {
                var resolution = tex0Resolver(tex0, 0);
                if (resolution.Checksum != 0)
                    resolved++;

                if (rows.Count < 64)
                {
                    rows.Add(new GsTex0CorrelationRow
                    {
                        Tex0 = $"0x{tex0:X16}",
                        XyzWrites = xyz,
                        TextureChecksum = resolution.Checksum == 0 ? null : resolution.Checksum,
                        ResolutionMode = resolution.Checksum == 0 ? null : resolution.ResolveMode
                    });
                }
            }

            return new GsTextureCorrelationAudit
            {
                UniqueRuntimeTex0 = xyzByTex0.Count,
                ResolvedTex0 = resolved,
                TopRuntimeTex0 = rows
            };
        }
    }

    private sealed record ReferencePixels(byte[] Pixels, int Width, int Height);
}
