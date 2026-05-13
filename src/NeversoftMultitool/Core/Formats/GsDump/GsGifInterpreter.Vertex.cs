using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.GsDump;

internal sealed partial class GsGifInterpreter
{
    private void ProcessPackedXyz(ReadOnlySpan<byte> qword, bool xyzf, bool forceNoKick = false)
    {
        var rawX = BinaryPrimitives.ReadUInt16LittleEndian(qword);
        var rawY = BinaryPrimitives.ReadUInt16LittleEndian(qword[4..]);
        var z = BinaryPrimitives.ReadUInt32LittleEndian(qword[8..]);
        if (xyzf)
        {
            z &= 0x00FFFFFF;
            state.Fog = (byte)((BinaryPrimitives.ReadUInt32LittleEndian(qword[12..]) >> 4) & 0xFF);
        }

        var noKick = forceNoKick || (BinaryPrimitives.ReadUInt32LittleEndian(qword[12..]) & 0x8000) != 0;
        ProcessXyz(rawX, rawY, z, noKick);
    }

    private void ProcessXyz(ulong value, bool xyzf, bool forceNoKick = false)
    {
        var rawX = (int)(value & 0xFFFF);
        var rawY = (int)((value >> 16) & 0xFFFF);
        var z = xyzf ? (uint)((value >> 32) & 0x00FFFFFF) : (uint)(value >> 32);
        if (xyzf)
            state.Fog = (byte)(value >> 56);
        ProcessXyz(rawX, rawY, z, forceNoKick);
    }

    private void ProcessXyz(int rawX, int rawY, uint z, bool noKick)
    {
        gifAudit.XyzWriteCount++;
        var primName = PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)];
        AddCount(gifAudit.XyzWritesByPrimitive, primName);
        AddCount(xyzByTex0, state.Context.Tex0);

        var (screenX, screenY) = Project(rawX, rawY);
        var vertex = new GsVertex(
            screenX,
            screenY,
            z,
            state.ColorR,
            state.ColorG,
            state.ColorB,
            state.ColorA,
            CurrentU(),
            CurrentV(),
            CurrentQ(),
            state.Fog,
            noKick);

        EmitVertex(vertex);
    }

    private (float X, float Y) Project(int rawX, int rawY)
    {
        var ofx = (int)(state.Context.XyOffset & 0xFFFF);
        var ofy = (int)((state.Context.XyOffset >> 32) & 0xFFFF);
        return ((rawX - ofx) / 16f * options.CoordinateScaleX, (rawY - ofy) / 16f * options.CoordinateScaleY);
    }

    private float CurrentU()
    {
        if (state.Fst)
        {
            var width = Math.Max(1, 1 << (int)((state.Context.Tex0 >> 26) & 0xF));
            return state.U / 16f / width;
        }

        return state.S;
    }

    private float CurrentV()
    {
        if (state.Fst)
        {
            var height = Math.Max(1, 1 << (int)((state.Context.Tex0 >> 30) & 0xF));
            return state.V / 16f / height;
        }

        return state.T;
    }

    private float CurrentQ()
    {
        return state.Fst ? 1f : state.Q;
    }

    private static bool IsDegenerateXy(GsVertex a, GsVertex b, GsVertex c)
    {
        // Compare in fixed-point screen-space (PCSX2 uses integer XY pre-offset for this cull).
        // Using float equality at this scale is fine because Project() produces deterministic
        // values from the same integer source.
        return (a.X == b.X && a.Y == b.Y)
               || (a.X == c.X && a.Y == c.Y)
               || (b.X == c.X && b.Y == c.Y);
    }

    private void EmitVertex(GsVertex vertex)
    {
        switch (state.PrimType)
        {
            case 0:
                if (!vertex.NoKick)
                    DrawPoint(vertex);
                break;
            case 3:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count == 3)
                {
                    if (!state.PrimitiveVertices.Any(static v => v.NoKick))
                        DrawTriangle(state.PrimitiveVertices[0], state.PrimitiveVertices[1],
                            state.PrimitiveVertices[2]);
                    state.PrimitiveVertices.Clear();
                }

                break;
            case 4:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count >= 3 && !vertex.NoKick)
                {
                    var n = state.PrimitiveVertices.Count;
                    var v0 = state.PrimitiveVertices[n - 3];
                    var v1 = state.PrimitiveVertices[n - 2];
                    var v2 = state.PrimitiveVertices[n - 1];
                    // Cull degenerate triangles (two vertices share the same XY). HUD font strips use
                    // this to restart between glyphs: a XYZ3 vertex with the same XY as a neighbour
                    // collapses the bridging triangle to zero area, which the PS2 GS culls. Without
                    // this cull, the bridge interpolates UV across atlas regions and scrambles text
                    // (e.g. SPECIAL / score meters).
                    if (!IsDegenerateXy(v0, v1, v2))
                    {
                        if ((n & 1) == 1)
                            DrawTriangle(v0, v1, v2);
                        else
                            DrawTriangle(v1, v0, v2);
                    }
                }

                break;
            case 5:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count >= 3 && !vertex.NoKick)
                {
                    var n = state.PrimitiveVertices.Count;
                    var v0 = state.PrimitiveVertices[0];
                    var v1 = state.PrimitiveVertices[n - 2];
                    var v2 = state.PrimitiveVertices[n - 1];
                    if (!IsDegenerateXy(v0, v1, v2))
                        DrawTriangle(v0, v1, v2);
                }

                break;
            case 6:
                state.PrimitiveVertices.Add(vertex);
                if (state.PrimitiveVertices.Count == 2)
                {
                    if (!state.PrimitiveVertices.Any(static v => v.NoKick))
                        DrawSprite(state.PrimitiveVertices[0], state.PrimitiveVertices[1]);
                    state.PrimitiveVertices.Clear();
                }

                break;
            default:
                NoteUnsupported($"primitive_{PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)].ToLowerInvariant()}");
                break;
        }
    }

    private void DrawPoint(GsVertex vertex)
    {
        renderAudit.DrawsSeen++;
        renderAudit.PointsDrawn++;

        var context = state.Context;
        var framebufferTarget = DecodeFramebufferTarget(context);
        var scissor = DecodeScissor(context);
        var x = (int)MathF.Floor(vertex.X);
        var y = (int)MathF.Floor(vertex.Y);
        if (x < scissor.X0 || x > scissor.X1 || y < scissor.Y0 || y > scissor.Y1 ||
            x < 0 || x >= options.Width || y < 0 || y >= options.Height)
        {
            return;
        }

        var texture = state.Tme ? ResolveTexture(context.Tex0) : null;
        if (state.Tme && texture == null)
        {
            renderAudit.TextureDecodeMisses++;
            RecordMissingTextureDraw(context.Tex0, framebufferTarget, x, y, x, y);
            BeginMaterialDraw("POINT", framebufferTarget, x, y, x, y, true, vertex, default, default, 1);
            NoteUnsupported("textured_draw_skipped_missing_texture");
            return;
        }

        if (state.Abe)
            NoteApproximation("gs_alpha_blend_approximated");
        if (!IsFramebufferPsmSupported(framebufferTarget.Psm))
            NoteUnsupported($"framebuffer_psm_0x{framebufferTarget.Psm:X2}");

        var material = BeginMaterialDraw("POINT", framebufferTarget, x, y, x, y, false, vertex, default, default, 1);
        RecordFramebufferDraw(framebufferTarget);
        var depthBuffer = GetDepthBuffer(context);
        var idx = y * options.Width + x;
        if (!PassesDepth(vertex.Z, idx, context, depthBuffer))
        {
            renderAudit.DepthRejectedPixels++;
            return;
        }

        var sample = Sample(
            context,
            texture,
            PointTextureCoordinate(vertex.U, vertex.Q),
            PointTextureCoordinate(vertex.V, vertex.Q));
        if (state.Tme)
        {
            if (state.Fst)
                renderAudit.FixedTexturePixels++;
            else
                renderAudit.PerspectiveTexturePixels++;
        }

        CombineTextureFunction(context, sample, vertex.R, vertex.G, vertex.B, vertex.A,
            out var srcR, out var srcG, out var srcB, out var srcA);
        if (state.Fge)
            ApplyFog(vertex.Fog, ref srcR, ref srcG, ref srcB);

        var alphaTest = EvaluateAlphaTest(srcA, context);
        if (alphaTest != GsAlphaTestResult.Pass)
        {
            renderAudit.AlphaFailedPixels++;
            RecordAlphaFailure(context.Tex0, framebufferTarget, alphaTest, x, y);
            switch (alphaTest)
            {
                case GsAlphaTestResult.FailFramebufferOnly:
                    renderAudit.AlphaFailFramebufferOnlyPixels++;
                    break;
                case GsAlphaTestResult.FailZBufferOnly:
                    renderAudit.AlphaFailZBufferOnlyPixels++;
                    if (!ZWriteMasked(context))
                    {
                        depthBuffer[idx] = vertex.Z;
                        WriteDepthToVram(context, x, y, vertex.Z);
                    }

                    return;
                case GsAlphaTestResult.FailRgbOnly:
                    renderAudit.AlphaFailRgbOnlyPixels++;
                    break;
                default:
                    return;
            }
        }

        if (alphaTest == GsAlphaTestResult.Pass && !ZWriteMasked(context))
        {
            depthBuffer[idx] = vertex.Z;
            WriteDepthToVram(context, x, y, vertex.Z);
        }

        var p = idx * 4;
        var extraFbmsk = alphaTest == GsAlphaTestResult.FailRgbOnly
            ? AlphaWriteMaskForPsm(framebufferTarget.Psm)
            : 0u;
        var probe = ShouldProbe(x, y, framebufferTarget);
        GsSample written;
        GsSample? preBlendDst = null;
        if (state.Abe)
        {
            if (probe && TryReadFramebufferPixel(framebufferTarget, x, y, out var dstSample))
                preBlendDst = dstSample;
            var blended = BlendPixel(context, framebufferTarget, x, y, p, srcR, srcG, srcB, srcA);
            written = WriteFramebufferPixel(
                framebufferTarget,
                context,
                x,
                y,
                ClampByte(blended.R),
                ClampByte(blended.G),
                ClampByte(blended.B),
                ClampByte(blended.A),
                extraFbmsk);
        }
        else
        {
            written = WriteFramebufferPixel(
                framebufferTarget,
                context,
                x,
                y,
                ClampByte(srcR),
                ClampByte(srcG),
                ClampByte(srcB),
                ClampByte(srcA),
                extraFbmsk);
        }

        pixels[p] = (byte)written.R;
        pixels[p + 1] = (byte)written.G;
        pixels[p + 2] = (byte)written.B;
        pixels[p + 3] = 255;
        if (probe)
            EmitPixelProbe(x, y, "POINT", framebufferTarget, context, state.Tme, sample,
                vertex.R, vertex.G, vertex.B, vertex.A, srcR, srcG, srcB, srcA, vertex.Z, preBlendDst, written);
        renderAudit.PixelsTouched++;
        material.PixelsWritten++;
        RecordFramebufferPixels(framebufferTarget, context.Tex0, 1, x, y, x, y);
        InvalidateTextureCacheForFramebufferWrite(framebufferTarget);
        MaybeSaveDrawRt(framebufferTarget);
    }

    private void DrawSprite(GsVertex a, GsVertex b)
    {
        renderAudit.DrawsSeen++;
        renderAudit.SpritesDrawn++;
        var v0 = a;
        var v1 = new GsVertex(b.X, a.Y, b.Z, a.R, a.G, a.B, a.A, b.U, a.V, b.Q, a.Fog, false);
        var v2 = new GsVertex(a.X, b.Y, b.Z, a.R, a.G, a.B, a.A, a.U, b.V, b.Q, a.Fog, false);
        var v3 = b;
        DrawTriangle(v0, v1, v2, false);
        DrawTriangle(v2, v1, v3, false);
        MaybeSaveDrawRt(DecodeFramebufferTarget(state.Context));
    }

    private void DrawTriangle(GsVertex a, GsVertex b, GsVertex c, bool countDraw = true)
    {
        if (countDraw)
            renderAudit.DrawsSeen++;

        var denom = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
        if (MathF.Abs(denom) < 0.0001f)
            return;

        var minX = MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)));
        var maxX = MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
        var minY = MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)));
        var maxY = MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
        var context = state.Context;
        var framebufferTarget = DecodeFramebufferTarget(context);
        var scissor = DecodeScissor(context);
        var x0 = Math.Max(scissor.X0, Math.Clamp((int)minX, 0, options.Width - 1));
        var x1 = Math.Min(scissor.X1, Math.Clamp((int)maxX, 0, options.Width - 1));
        var y0 = Math.Max(scissor.Y0, Math.Clamp((int)minY, 0, options.Height - 1));
        var y1 = Math.Min(scissor.Y1, Math.Clamp((int)maxY, 0, options.Height - 1));
        if (x0 > x1 || y0 > y1)
            return;

        var primitiveName = PrimitiveNames[Math.Clamp(state.PrimType, 0, 7)];
        var invDenom = 1f / denom;
        var texture = state.Tme ? ResolveTexture(context.Tex0) : null;
        if (state.Tme && texture == null)
        {
            renderAudit.TextureDecodeMisses++;
            RecordMissingTextureDraw(context.Tex0, framebufferTarget, x0, y0, x1, y1);
            BeginMaterialDraw(primitiveName, framebufferTarget, x0, y0, x1, y1, true, a, b, c, 3);
            NoteUnsupported("textured_draw_skipped_missing_texture");
            return;
        }

        if (state.Abe)
            NoteApproximation("gs_alpha_blend_approximated");
        if (!IsFramebufferPsmSupported(framebufferTarget.Psm))
            NoteUnsupported($"framebuffer_psm_0x{framebufferTarget.Psm:X2}");

        var material = BeginMaterialDraw(primitiveName, framebufferTarget, x0, y0, x1, y1, false, a, b, c, 3);
        RecordFramebufferDraw(framebufferTarget);
        var depthBuffer = GetDepthBuffer(context);
        long pixelsWritten = 0;
        var writeMinX = options.Width;
        var writeMinY = options.Height;
        var writeMaxX = -1;
        var writeMaxY = -1;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var px = x + 0.5f;
                var py = y + 0.5f;
                var w0 = ((b.Y - c.Y) * (px - c.X) + (c.X - b.X) * (py - c.Y)) * invDenom;
                var w1 = ((c.Y - a.Y) * (px - c.X) + (a.X - c.X) * (py - c.Y)) * invDenom;
                var w2 = 1f - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0)
                    continue;

                var z = a.Z * w0 + b.Z * w1 + c.Z * w2;
                var idx = y * options.Width + x;
                if (!PassesDepth(z, idx, context, depthBuffer))
                {
                    renderAudit.DepthRejectedPixels++;
                    continue;
                }

                var sample = Sample(
                    context,
                    texture,
                    InterpolateTextureCoordinate(a.U, b.U, c.U, a.Q, b.Q, c.Q, w0, w1, w2),
                    InterpolateTextureCoordinate(a.V, b.V, c.V, a.Q, b.Q, c.Q, w0, w1, w2));
                if (state.Tme)
                {
                    if (state.Fst)
                        renderAudit.FixedTexturePixels++;
                    else
                        renderAudit.PerspectiveTexturePixels++;
                }

                var vr = a.R * w0 + b.R * w1 + c.R * w2;
                var vg = a.G * w0 + b.G * w1 + c.G * w2;
                var vb = a.B * w0 + b.B * w1 + c.B * w2;
                var va = a.A * w0 + b.A * w1 + c.A * w2;
                CombineTextureFunction(context, sample, vr, vg, vb, va,
                    out var srcR, out var srcG, out var srcB, out var srcA);
                if (state.Fge)
                {
                    var fog = a.Fog * w0 + b.Fog * w1 + c.Fog * w2;
                    ApplyFog(fog, ref srcR, ref srcG, ref srcB);
                }

                var alphaTest = EvaluateAlphaTest(srcA, context);
                if (alphaTest != GsAlphaTestResult.Pass)
                {
                    renderAudit.AlphaFailedPixels++;
                    RecordAlphaFailure(context.Tex0, framebufferTarget, alphaTest, x, y);
                    switch (alphaTest)
                    {
                        case GsAlphaTestResult.FailFramebufferOnly:
                            renderAudit.AlphaFailFramebufferOnlyPixels++;
                            break;
                        case GsAlphaTestResult.FailZBufferOnly:
                            renderAudit.AlphaFailZBufferOnlyPixels++;
                            if (!ZWriteMasked(context))
                            {
                                depthBuffer[idx] = z;
                                WriteDepthToVram(context, x, y, z);
                            }

                            continue;
                        case GsAlphaTestResult.FailRgbOnly:
                            renderAudit.AlphaFailRgbOnlyPixels++;
                            break;
                        default:
                            continue;
                    }
                }

                if (alphaTest == GsAlphaTestResult.Pass && !ZWriteMasked(context))
                {
                    depthBuffer[idx] = z;
                    WriteDepthToVram(context, x, y, z);
                }

                var p = idx * 4;
                var extraFbmsk = alphaTest == GsAlphaTestResult.FailRgbOnly
                    ? AlphaWriteMaskForPsm(framebufferTarget.Psm)
                    : 0u;
                var probe = ShouldProbe(x, y, framebufferTarget);
                if (state.Abe)
                {
                    GsSample? preBlendDst = null;
                    if (probe && TryReadFramebufferPixel(framebufferTarget, x, y, out var dstSample))
                        preBlendDst = dstSample;
                    var blended = BlendPixel(context, framebufferTarget, x, y, p, srcR, srcG, srcB, srcA);
                    var written = WriteFramebufferPixel(
                        framebufferTarget,
                        context,
                        x,
                        y,
                        ClampByte(blended.R),
                        ClampByte(blended.G),
                        ClampByte(blended.B),
                        ClampByte(blended.A),
                        extraFbmsk);
                    pixels[p] = (byte)written.R;
                    pixels[p + 1] = (byte)written.G;
                    pixels[p + 2] = (byte)written.B;
                    pixels[p + 3] = 255;
                    if (probe)
                        EmitPixelProbe(x, y, "TRIANGLE", framebufferTarget, context, state.Tme, sample,
                            vr, vg, vb, va, srcR, srcG, srcB, srcA, z, preBlendDst, written);
                }
                else
                {
                    var outR = (byte)Math.Clamp((int)MathF.Round(srcR), 0, 255);
                    var outG = (byte)Math.Clamp((int)MathF.Round(srcG), 0, 255);
                    var outB = (byte)Math.Clamp((int)MathF.Round(srcB), 0, 255);
                    var outA = (byte)Math.Clamp((int)MathF.Round(srcA), 0, 255);
                    var written = WriteFramebufferPixel(framebufferTarget, context, x, y, outR, outG, outB, outA,
                        extraFbmsk);
                    pixels[p] = (byte)written.R;
                    pixels[p + 1] = (byte)written.G;
                    pixels[p + 2] = (byte)written.B;
                    pixels[p + 3] = 255;
                    if (probe)
                        EmitPixelProbe(x, y, "TRIANGLE", framebufferTarget, context, state.Tme, sample,
                            vr, vg, vb, va, srcR, srcG, srcB, srcA, z, null, written);
                }

                renderAudit.PixelsTouched++;
                pixelsWritten++;
                writeMinX = Math.Min(writeMinX, x);
                writeMinY = Math.Min(writeMinY, y);
                writeMaxX = Math.Max(writeMaxX, x);
                writeMaxY = Math.Max(writeMaxY, y);
            }
        }

        RecordFramebufferPixels(framebufferTarget, context.Tex0, pixelsWritten, writeMinX, writeMinY, writeMaxX,
            writeMaxY);
        if (pixelsWritten > 0)
            InvalidateTextureCacheForFramebufferWrite(framebufferTarget);
        material.PixelsWritten += pixelsWritten;
        renderAudit.TrianglesDrawn++;
        if (countDraw)
            MaybeSaveDrawRt(framebufferTarget);
    }
}
