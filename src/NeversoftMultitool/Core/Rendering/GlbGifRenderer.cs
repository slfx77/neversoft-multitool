using SharpGLTF.Schema2;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;

namespace NeversoftMultitool.Core.Rendering;

internal static class GlbGifRenderer
{
    public static (int FrameCount, float Duration) RenderToFile(
        string glbPath, string gifPath, int longEdge = 256, int fps = 15,
        float azimuthDeg = -90f, float elevationDeg = 10f,
        int? animationIndex = null)
    {
        var model = ModelRoot.Load(glbPath);

        Animation? anim;
        if (animationIndex.HasValue)
            anim = model.LogicalAnimations[animationIndex.Value];
        else if (model.LogicalAnimations.Count > 0)
            anim = model.LogicalAnimations[0];
        else
            anim = null;

        if (anim == null || anim.Duration <= 0)
            return (0, 0f);

        var duration = anim.Duration;
        var frameCount = Math.Max(1, (int)(duration * fps));
        var delayCentiseconds = Math.Max(1, (int)Math.Round(100.0 / fps));

        // Pre-pass: compute union of projected bounds across all frames so the
        // viewport stays stable and nothing gets clipped mid-animation.
        var (width, height, unionW, unionH) = ComputeStableFrameSize(
            model, anim, duration, frameCount, longEdge, azimuthDeg, elevationDeg);

        Image<Rgba32>? gif = null;

        try
        {
            for (var i = 0; i < frameCount; i++)
            {
                var time = duration * i / frameCount;
                using var frame = RenderFrame(model, anim, time, longEdge,
                    azimuthDeg, elevationDeg, width, height, unionW, unionH);

                if (gif == null)
                {
                    gif = frame.Clone();
                    gif.Metadata.GetGifMetadata().RepeatCount = 0;
                    gif.Metadata.GetGifMetadata().ColorTableMode = GifColorTableMode.Local;
                    var rootMeta = gif.Frames.RootFrame.Metadata.GetGifMetadata();
                    rootMeta.FrameDelay = delayCentiseconds;
                    rootMeta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
                }
                else
                {
                    var added = gif.Frames.AddFrame(frame.Frames.RootFrame);
                    var meta = added.Metadata.GetGifMetadata();
                    meta.FrameDelay = delayCentiseconds;
                    meta.DisposalMethod = GifDisposalMethod.RestoreToBackground;
                }
            }

            if (gif != null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(gifPath))!);
                gif.SaveAsGif(gifPath);
            }

            return (frameCount, duration);
        }
        finally
        {
            gif?.Dispose();
        }
    }

    private static (int Width, int Height, float UnionW, float UnionH) ComputeStableFrameSize(
        ModelRoot model, Animation anim, float duration, int frameCount,
        int longEdge, float azimuthDeg, float elevationDeg)
    {
        float unionMinX = float.MaxValue, unionMinY = float.MaxValue;
        float unionMaxX = float.MinValue, unionMaxY = float.MinValue;

        for (var i = 0; i < frameCount; i++)
        {
            var time = duration * i / frameCount;
            var scene = GlbModelLoader.Load(model, anim, time);
            if (!scene.HasGeometry) continue;

            var (minX, minY, w, h) = GlbRenderer.ComputeProjectedBounds(
                scene, azimuthDeg, elevationDeg);

            if (w <= 0 && h <= 0) continue;
            if (minX < unionMinX) unionMinX = minX;
            if (minY < unionMinY) unionMinY = minY;
            if (minX + w > unionMaxX) unionMaxX = minX + w;
            if (minY + h > unionMaxY) unionMaxY = minY + h;
        }

        var totalW = unionMaxX - unionMinX;
        var totalH = unionMaxY - unionMinY;

        if (totalW < 0.001f && totalH < 0.001f)
            return (longEdge, longEdge, 0f, 0f);

        int width, height;
        if (totalW >= totalH)
        {
            width = longEdge;
            height = Math.Max(1, (int)(longEdge * totalH / totalW));
        }
        else
        {
            height = longEdge;
            width = Math.Max(1, (int)(longEdge * totalW / totalH));
        }

        return (width, height, totalW, totalH);
    }

    private static Image<Rgba32> RenderFrame(
        ModelRoot model, Animation animation, float time,
        int longEdge, float azimuthDeg, float elevationDeg,
        int fixedWidth, int fixedHeight, float referenceW, float referenceH)
    {
        var scene = GlbModelLoader.Load(model, animation, time);
        if (!scene.HasGeometry)
            return new Image<Rgba32>(fixedWidth, fixedHeight);

        return GlbRenderer.RenderScene(scene, longEdge, azimuthDeg, elevationDeg,
            fixedWidth: fixedWidth, fixedHeight: fixedHeight,
            referenceWidth: referenceW, referenceHeight: referenceH);
    }
}
