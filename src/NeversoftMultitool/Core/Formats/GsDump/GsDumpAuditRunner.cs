using System.Globalization;
using System.Text;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Mesh.Ps2Scene.Geom;
using NeversoftMultitool.Core.Formats.Texture.Ps2;
using NeversoftMultitool.Core.Formats.Texture.Ps2Scene;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed class GsDumpAuditOptions
{
    public string? PngPath { get; init; }
    public string? TexturePath { get; init; }
    public bool JsonOnly { get; init; }
    public bool Verbose { get; init; }
    public int? ProbeX { get; init; }
    public int? ProbeY { get; init; }
    public uint? ProbeFbp { get; init; }
    public string? ProbeOutputPath { get; init; }
    public int? MaxVsync { get; init; }
    public string? SaveRtDir { get; init; }
    public int SaveRtStart { get; init; }
    public int? SaveRtCount { get; init; }
    public uint? SaveRtFbp { get; init; }
}

internal static class GsDumpAuditRunner
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
                probeWriter = new StreamWriter(probePath, append: false);
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
                    SaveRtSink = options.SaveRtDir == null ? null : snapshot =>
                    {
                        var saveDir = options.SaveRtDir;
                        Directory.CreateDirectory(saveDir);
                        var fileName = $"{snapshot.DrawIndex:D5}_rt_{snapshot.Fbp:X5}_{(snapshot.Psm == Ps2TexPixelDecoder.PSMCT16 || snapshot.Psm == Ps2GsVram.PSMCT16S ? "C_16" : snapshot.Psm == Ps2TexPixelDecoder.PSMCT24 ? "C_24" : "C_32")}.png";
                        var path = Path.Combine(saveDir, fileName);
                        SaveRgba(path, snapshot.Rgba, snapshot.Width, snapshot.Height);
                    },
                    TextureDumpSink = options.JsonOnly ? null : dumpTexture =>
                    {
                        Directory.CreateDirectory(textureDumpDirectory);
                        var fileName =
                            $"{textureDumpIndex++:D4}_tex0-{dumpTexture.Audit.Tex0[2..]}_texa-{dumpTexture.Audit.Texa[2..]}_psm-{dumpTexture.Audit.Psm:X2}_{dumpTexture.Audit.Width}x{dumpTexture.Audit.Height}_at-{dumpTexture.Audit.RegionX}-{dumpTexture.Audit.RegionY}_{MakeSafeFileSuffix(dumpTexture.Audit.Source)}_{dumpTexture.Audit.ContentHash:X8}.png";
                        var path = Path.Combine(textureDumpDirectory, fileName);
                        SaveRgba(path, dumpTexture.Rgba, dumpTexture.Audit.Width, dumpTexture.Audit.Height);
                        return Path.GetFullPath(path);
                    }
                });
            return FinishRun(interpretation, dump, gsPath, outputDirectory, options, dimensions, pngPath, stem, textureContext, textureDumpDirectory);
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

        interpretation.Render.DirectNonBlackBounds = BuildPixelBounds(directPixels, dimensions.Width, dimensions.Height);
        interpretation.Render.PresentedNonBlackBounds = BuildPixelBounds(renderPixels, dimensions.Width, dimensions.Height);

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
                SaveRgba(embeddedScreenshotPath, ConvertEmbeddedScreenshotToRgba(dump), dump.ScreenshotWidth, dump.ScreenshotHeight);
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

        File.WriteAllText(jsonPath, System.Text.Json.JsonSerializer.Serialize(
            report,
            GsDumpAuditJsonContext.Default.GsDumpAuditReport));
        return report;
    }

    private static string? ResolvePngPath(string gsPath, string? explicitPng)
    {
        if (!string.IsNullOrWhiteSpace(explicitPng))
            return File.Exists(explicitPng) ? explicitPng : null;

        var sibling = Path.ChangeExtension(gsPath, ".png");
        return File.Exists(sibling) ? sibling : null;
    }

    private static (int Width, int Height) ResolveRenderDimensions(GsDumpFile dump, string? pngPath)
    {
        if (dump.ScreenshotWidth > 0 && dump.ScreenshotHeight > 0)
            return (dump.ScreenshotWidth, dump.ScreenshotHeight);

        if (pngPath != null)
        {
            var image = Image.Identify(pngPath);
            if (image != null)
                return (image.Width, image.Height);
        }
        return (640, 448);
    }

    private static (float X, float Y) ResolveCoordinateScale(GsDumpFile dump)
    {
        _ = dump;
        return (1f, 1f);
    }

    private static bool HasEmbeddedScreenshot(GsDumpFile dump) =>
        dump.ScreenshotWidth > 0 &&
        dump.ScreenshotHeight > 0 &&
        dump.ScreenshotPixels.Length == dump.ScreenshotWidth * dump.ScreenshotHeight * 4;

    private static ReferencePixels? LoadReferencePixels(GsDumpFile dump, string? pngPath)
    {
        if (HasEmbeddedScreenshot(dump))
            return new ReferencePixels(
                ConvertEmbeddedScreenshotToRgba(dump),
                dump.ScreenshotWidth,
                dump.ScreenshotHeight);

        if (pngPath == null)
            return null;

        using var reference = Image.Load<Rgba32>(pngPath);
        var pixels = new byte[reference.Width * reference.Height * 4];
        reference.CopyPixelDataTo(pixels);
        return new ReferencePixels(pixels, reference.Width, reference.Height);
    }

    private static bool TryFitToReferencePresentation(
        byte[] sourcePixels,
        int width,
        int height,
        GsPixelBounds? sourceBounds,
        GsPixelBounds? referenceBounds,
        out byte[] fittedPixels)
    {
        fittedPixels = sourcePixels;
        if (sourceBounds == null || referenceBounds == null)
            return false;

        if (!ShouldFitToReferencePresentation(sourceBounds, referenceBounds, width, height))
            return false;

        var crop = new Rectangle(sourceBounds.X, sourceBounds.Y, sourceBounds.Width, sourceBounds.Height);
        var target = new Point(referenceBounds.X, referenceBounds.Y);
        using var sourceImage = Image.LoadPixelData<Rgba32>(sourcePixels, width, height);
        sourceImage.Mutate(ctx => ctx
            .Crop(crop)
            .Resize(referenceBounds.Width, referenceBounds.Height));

        using var canvas = new Image<Rgba32>(width, height, new Rgba32(0, 0, 0, 255));
        canvas.Mutate(ctx => ctx.DrawImage(sourceImage, target, 1f));

        fittedPixels = new byte[width * height * 4];
        canvas.CopyPixelDataTo(fittedPixels);
        return true;
    }

    private static bool ShouldFitToReferencePresentation(
        GsPixelBounds source,
        GsPixelBounds reference,
        int width,
        int height)
    {
        if (source.Width <= 0 || source.Height <= 0 || reference.Width <= 0 || reference.Height <= 0)
            return false;

        var alreadyAligned =
            Math.Abs(source.X - reference.X) <= 2 &&
            Math.Abs(source.Y - reference.Y) <= 2 &&
            Math.Abs(source.Width - reference.Width) <= 2 &&
            Math.Abs(source.Height - reference.Height) <= 2;
        if (alreadyAligned)
            return false;

        var sourceNearlyFullWidth = source.Width >= width * 0.90;
        var referenceNearlyFullWidth = reference.Width >= width * 0.90;
        var sourceMostlyFullHeight = source.Height >= height * 0.80;
        var referenceLetterboxed = reference.Height <= source.Height - 24 && reference.Y >= 16;
        return sourceNearlyFullWidth &&
               referenceNearlyFullWidth &&
               sourceMostlyFullHeight &&
               referenceLetterboxed;
    }

    private static byte[] ConvertEmbeddedScreenshotToRgba(GsDumpFile dump)
    {
        var rgba = new byte[dump.ScreenshotPixels.Length];
        for (var i = 0; i < dump.ScreenshotPixels.Length; i += 4)
        {
            rgba[i] = dump.ScreenshotPixels[i];
            rgba[i + 1] = dump.ScreenshotPixels[i + 1];
            rgba[i + 2] = dump.ScreenshotPixels[i + 2];
            rgba[i + 3] = 255;
        }

        return rgba;
    }

    private static void AddCount(Dictionary<string, long> counts, string key)
    {
        counts.TryGetValue(key, out var current);
        counts[key] = current + 1;
    }

    private static GsTextureContext? BuildTextureContext(string? texturePath)
    {
        if (string.IsNullOrWhiteSpace(texturePath))
            return null;

        if (!ZoneTextureCatalog.TryBuild(texturePath, out var catalog) || catalog == null)
            return null;

        return new GsTextureContext(catalog.CreateTextureResolver(), catalog.CreateDebugTex0Resolver(texturePath));
    }

    private static Dictionary<string, long> BuildPacketTypeCounts(GsDumpFile dump) =>
        dump.Packets
            .GroupBy(static packet => packet.Kind.ToString())
            .OrderBy(static group => group.Key, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => (long)group.Count());

    private static Dictionary<string, GsTransferStats> BuildTransferStats(GsDumpFile dump)
    {
        var stats = new Dictionary<string, GsTransferStats>(StringComparer.Ordinal);
        foreach (var packet in dump.Packets.Where(static packet => packet.Kind == GsDumpPacketKind.Transfer))
        {
            var key = packet.Path?.ToString() ?? "Unknown";
            if (!stats.TryGetValue(key, out var row))
            {
                row = new GsTransferStats();
                stats[key] = row;
            }

            row.Packets++;
            row.Bytes += packet.Data.Length;
        }

        return stats;
    }

    private static void SaveRgba(string path, byte[] pixels, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(pixels, width, height);
        image.SaveAsPng(path);
    }

    private static void SaveMaterialCsv(string path, IReadOnlyList<GsMaterialAuditRow> materials)
    {
        var csv = new StringBuilder();
        AppendCsvRow(
            csv,
            "primitive",
            "draws",
            "pixels_written",
            "missing_texture_draws",
            "bounds",
            "tex0",
            "texture_psm",
            "texture_size",
            "texture_tbp",
            "texture_tbw",
            "texture_tcc",
            "texture_tfx",
            "texture_cbp",
            "texture_cpsm",
            "texture_csm",
            "texture_csa",
            "texture_cld",
            "tex1",
            "clamp",
            "wms",
            "wmt",
            "min_u_or_mask",
            "max_u_or_fix",
            "min_v_or_mask",
            "max_v_or_fix",
            "alpha",
            "alpha_a",
            "alpha_b",
            "alpha_c",
            "alpha_d",
            "alpha_fix",
            "test",
            "ate",
            "atst",
            "aref",
            "afail",
            "zte",
            "ztst",
            "texa",
            "ta0",
            "aem",
            "ta1",
            "fog_color",
            "framebuffer",
            "fbp",
            "fbw",
            "fb_psm",
            "fb_mask",
            "zbuf",
            "zbp",
            "zpsm",
            "zmask",
            "scissor",
            "dither",
            "fba",
            "coord_mode",
            "avg_rgb",
            "min_rgb",
            "max_rgb",
            "avg_a",
            "uv_range",
            "q_range",
            "prim",
            "key");

        foreach (var material in materials)
        {
            AppendCsvRow(
                csv,
                material.Primitive,
                material.Draws,
                material.PixelsWritten,
                material.MissingTextureDraws,
                FormatBounds(material.Bounds),
                material.Tex0,
                $"0x{material.TexturePsm:X2}",
                $"{material.TextureWidth}x{material.TextureHeight}",
                material.TextureTbp,
                material.TextureTbw,
                material.TextureTcc,
                material.TextureTfx,
                material.TextureCbp,
                $"0x{material.TextureCpsm:X2}",
                material.TextureCsm,
                material.TextureCsa,
                material.TextureCld,
                material.Tex1,
                material.Clamp,
                material.ClampWms,
                material.ClampWmt,
                material.ClampMinUOrMask,
                material.ClampMaxUOrFix,
                material.ClampMinVOrMask,
                material.ClampMaxVOrFix,
                material.Alpha,
                material.AlphaA,
                material.AlphaB,
                material.AlphaC,
                material.AlphaD,
                material.AlphaFix,
                material.Test,
                material.AlphaTestEnabled,
                material.AlphaTestMethod,
                material.AlphaRef,
                material.AlphaFailMode,
                material.DepthTestEnabled,
                material.DepthTestMethod,
                material.Texa,
                material.TexaTa0,
                material.TexaAem,
                material.TexaTa1,
                material.FogColor,
                material.FramebufferKey,
                material.FramebufferFbp,
                material.FramebufferFbw,
                $"0x{material.FramebufferPsm:X2}",
                $"0x{material.FramebufferMask:X8}",
                material.Zbuf,
                material.Zbp,
                $"0x{material.Zpsm:X2}",
                material.Zmask,
                $"{material.ScissorX0},{material.ScissorY0}-{material.ScissorX1},{material.ScissorY1}",
                material.DitherEnabled,
                material.FramebufferAlphaWriteEnabled,
                material.FixedTextureCoordinates ? "UV/FST" : "STQ",
                $"{FormatDouble(material.AvgR)}/{FormatDouble(material.AvgG)}/{FormatDouble(material.AvgB)}",
                $"{FormatDouble(material.MinR)}/{FormatDouble(material.MinG)}/{FormatDouble(material.MinB)}",
                $"{FormatDouble(material.MaxR)}/{FormatDouble(material.MaxG)}/{FormatDouble(material.MaxB)}",
                FormatDouble(material.AvgA),
                $"{FormatDouble(material.MinU)},{FormatDouble(material.MinV)}-{FormatDouble(material.MaxU)},{FormatDouble(material.MaxV)}",
                $"{FormatDouble(material.MinQ)}-{FormatDouble(material.MaxQ)}",
                material.Prim,
                material.Key);
        }

        File.WriteAllText(path, csv.ToString());
    }

    private static void AppendCsvRow(StringBuilder csv, params object?[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (i != 0)
                csv.Append(',');
            csv.Append(CsvValue(values[i]));
        }

        csv.AppendLine();
    }

    private static string CsvValue(object? value)
    {
        var text = value switch
        {
            null => "",
            bool b => b ? "1" : "0",
            double d => FormatDouble(d),
            float f => f.ToString("0.###", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? ""
        };

        if (!text.Contains(',') && !text.Contains('"') && !text.Contains('\n') && !text.Contains('\r'))
            return text;

        return "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string FormatDouble(double value) =>
        value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string FormatBounds(GsPixelBounds? bounds) =>
        bounds == null
            ? ""
            : $"{bounds.X},{bounds.Y},{bounds.Width}x{bounds.Height}";

    private static GsPixelDiffStats CompareAgainstPng(
        byte[] renderPixels,
        int width,
        int height,
        string pngPath,
        string? diffPath)
    {
        using var reference = Image.Load<Rgba32>(pngPath);
        if (reference.Width != width || reference.Height != height)
            reference.Mutate(ctx => ctx.Resize(width, height));

        var referencePixels = new byte[width * height * 4];
        reference.CopyPixelDataTo(referencePixels);
        return CompareAgainstPixels(renderPixels, width, height, referencePixels, width, height, diffPath);
    }

    private static GsPixelDiffStats CompareAgainstPixels(
        byte[] renderPixels,
        int width,
        int height,
        byte[] referencePixels,
        int referenceWidth,
        int referenceHeight,
        string? diffPath)
    {
        if (referenceWidth != width || referenceHeight != height)
        {
            using var referenceImage = Image.LoadPixelData<Rgba32>(referencePixels, referenceWidth, referenceHeight);
            referenceImage.Mutate(ctx => ctx.Resize(width, height));
            referencePixels = new byte[width * height * 4];
            referenceImage.CopyPixelDataTo(referencePixels);
        }

        var diffPixels = new byte[width * height * 4];
        double absSum = 0;
        double squareSum = 0;
        var max = 0;
        var channelCount = width * height * 3;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                var r = Math.Abs(renderPixels[i] - referencePixels[i]);
                var g = Math.Abs(renderPixels[i + 1] - referencePixels[i + 1]);
                var b = Math.Abs(renderPixels[i + 2] - referencePixels[i + 2]);
                absSum += r + g + b;
                squareSum += r * r + g * g + b * b;
                max = Math.Max(max, Math.Max(r, Math.Max(g, b)));
                diffPixels[i] = (byte)Math.Clamp(r * 4, 0, 255);
                diffPixels[i + 1] = (byte)Math.Clamp(g * 4, 0, 255);
                diffPixels[i + 2] = (byte)Math.Clamp(b * 4, 0, 255);
                diffPixels[i + 3] = 255;
            }
        }

        if (diffPath != null)
            SaveRgba(diffPath, diffPixels, width, height);

        return new GsPixelDiffStats
        {
            Width = width,
            Height = height,
            MeanAbsoluteError = channelCount == 0 ? 0 : absSum / channelCount,
            RootMeanSquareError = channelCount == 0 ? 0 : Math.Sqrt(squareSum / channelCount),
            MaxChannelDifference = max,
            RenderBounds = BuildPixelBounds(renderPixels, width, height),
            ReferenceBounds = BuildPixelBounds(referencePixels, width, height),
            TopMismatchRegions = BuildMismatchRegions(diffPixels, width, height)
        };
    }

    private static GsPixelBounds? BuildPixelBounds(byte[] pixels, int width, int height)
    {
        var minX = width;
        var minY = height;
        var maxX = -1;
        var maxY = -1;
        long count = 0;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = (y * width + x) * 4;
                if (pixels[i] == 0 && pixels[i + 1] == 0 && pixels[i + 2] == 0)
                    continue;

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
                count++;
            }
        }

        return count == 0
            ? null
            : new GsPixelBounds
            {
                X = minX,
                Y = minY,
                Width = maxX - minX + 1,
                Height = maxY - minY + 1,
                NonBlackPixels = count
            };
    }

    private static List<GsMismatchRegion> BuildMismatchRegions(byte[] diffPixels, int width, int height)
    {
        const int tile = 32;
        var regions = new List<GsMismatchRegion>();
        for (var y0 = 0; y0 < height; y0 += tile)
        {
            for (var x0 = 0; x0 < width; x0 += tile)
            {
                var w = Math.Min(tile, width - x0);
                var h = Math.Min(tile, height - y0);
                double sum = 0;
                for (var y = y0; y < y0 + h; y++)
                {
                    for (var x = x0; x < x0 + w; x++)
                    {
                        var i = (y * width + x) * 4;
                        sum += (diffPixels[i] + diffPixels[i + 1] + diffPixels[i + 2]) / 4.0;
                    }
                }

                regions.Add(new GsMismatchRegion
                {
                    X = x0,
                    Y = y0,
                    Width = w,
                    Height = h,
                    MeanAbsoluteError = sum / (w * h * 3)
                });
            }
        }

        return regions
            .OrderByDescending(static region => region.MeanAbsoluteError)
            .Take(10)
            .ToList();
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
