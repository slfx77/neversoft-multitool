using System.Numerics;

namespace NeversoftMultitool.Core.Formats.Mesh.Psx;

/// <summary>
///     Applies a deterministic static state of the PSX-era renderer's tagged
///     texture-wibble (tag 6) and colour-pulse (tag 7) tables. The binary
///     contracts and runtime interpolation are decompilation-verified in
///     <c>docs/wibbly_texture_animation.md</c> in the THPS2 matching project.
///     Colour pulses evaluate the serialized playhead and accumulator as the
///     authored pre-tick state — some assets deliberately start partway
///     through a key interval (fire that would be black at key zero).
///     Whether that state is BAKED into the palette depends on the file class:
///     EVERY loaded region ticks its pulses every frame — M3d_RenderSetup
///     calls M3d_PreprocessPulsingColours for both env regions, the
///     <c>_o.psx</c> object-bank region, AND the items region (THPS2 proto
///     0x80095740-70; Spider-Man final 0x80076228-58; the only gates are
///     "region loaded" and "has pulse data") — so ALL parses bake. The
///     2026-07-22 raw-palette rule for banks mis-attributed the l1a1 "?"'s
///     in-game dark blue to the bank copy; that colour is the ITEMS-region
///     copy's own staggered-blue pulse, and the bank duplicate never reaches
///     output (POWERUP suppression + items substitution). Fire/star bank art
///     serializes BLACK with the pulse as its only colour source (corrected
///     2026-07-29). Pulse metadata is parsed either way.
/// </summary>
internal static class PsxSurfaceAnimationReader
{
    private const uint StopTag = uint.MaxValue;
    private const uint TextureWibbleTag = 6;
    private const uint ColourPulseTag = 7;
    private const int ObjectTableOffset = 12;
    private const int ObjectRecordSize = 36;
    private const int WibbleHeaderSize = 16;
    private const int WibbleFaceSize = 16;

    internal static PsxSurfaceAnimationResult ApplyStaticFrame(
        BinaryReader reader,
        ushort version,
        IReadOnlyList<PsxMeshObject> objects,
        IReadOnlyList<PsxMesh> meshes,
        Vector4[]? sourcePalette,
        bool bakeColourPulses)
    {
        var palette = sourcePalette;
        var pulses = new List<PsxColourPulse>();
        var stream = reader.BaseStream;
        var savedPosition = stream.Position;
        try
        {
            if (!stream.CanSeek || stream.Length < 8)
                return new PsxSurfaceAnimationResult(palette, pulses);

            stream.Seek(4, SeekOrigin.Begin);
            var metaTop = reader.ReadUInt32();
            if (metaTop < 8 || metaTop + 4L > stream.Length)
                return new PsxSurfaceAnimationResult(palette, pulses);

            stream.Seek(metaTop, SeekOrigin.Begin);
            for (var chunkCount = 0; chunkCount < 256 && stream.Position + 4 <= stream.Length; chunkCount++)
            {
                var tag = reader.ReadUInt32();
                if (tag == StopTag)
                    break;
                if (stream.Position + 4 > stream.Length)
                    break;

                var length = reader.ReadUInt32();
                var chunkStart = stream.Position;
                var chunkEnd = chunkStart + length;
                if (chunkEnd < chunkStart || chunkEnd > stream.Length)
                    break;

                switch (tag)
                {
                    case TextureWibbleTag:
                        ApplyTextureWibbles(reader, chunkEnd, version, objects, meshes);
                        break;
                    case ColourPulseTag when palette is { Length: > 0 }:
                        if (bakeColourPulses)
                            palette = (Vector4[])palette.Clone();
                        ReadColourPulses(
                            reader,
                            chunkEnd,
                            bakeColourPulses ? palette : null,
                            pulses);
                        break;
                }

                stream.Seek(chunkEnd, SeekOrigin.Begin);
            }
        }
        catch (EndOfStreamException)
        {
            // Malformed optional animation metadata must not invalidate the
            // otherwise usable static mesh.
        }
        finally
        {
            if (stream.CanSeek)
                stream.Seek(savedPosition, SeekOrigin.Begin);
        }

        return new PsxSurfaceAnimationResult(palette, pulses);
    }

    private static void ApplyTextureWibbles(
        BinaryReader reader,
        long chunkEnd,
        ushort version,
        IReadOnlyList<PsxMeshObject> objects,
        IReadOnlyList<PsxMesh> meshes)
    {
        var stream = reader.BaseStream;
        while (stream.Position + WibbleHeaderSize <= chunkEnd)
        {
            var itemOffset = reader.ReadUInt32();
            if (itemOffset == 0)
                break;

            var uVelocity = reader.ReadInt16();
            var vVelocity = reader.ReadInt16();
            var frequency = reader.ReadInt32();
            var faceCount = reader.ReadUInt16();
            var zeroUAmplitudes = reader.ReadByte() != 0;
            var zeroVAmplitudes = reader.ReadByte() != 0;
            var faceDataStart = stream.Position;
            var faceDataEnd = faceDataStart + (long)faceCount * WibbleFaceSize;
            if (faceDataEnd > chunkEnd)
                break;

            var objectIndex = ResolveObjectIndex(itemOffset, objects.Count);
            PsxMesh? mesh = null;
            if (objectIndex >= 0)
            {
                var meshIndex = objects[objectIndex].MeshIndex;
                if (meshIndex < meshes.Count)
                    mesh = meshes[meshIndex];
            }

            for (var rawFaceIndex = 0; rawFaceIndex < faceCount; rawFaceIndex++)
            {
                var vertices = new PsxTextureWibbleVertex[4];
                for (var slot = 0; slot < vertices.Length; slot++)
                {
                    vertices[slot] = new PsxTextureWibbleVertex(
                        reader.ReadByte(),
                        reader.ReadByte(),
                        reader.ReadByte(),
                        reader.ReadByte());
                }

                if (mesh == null || rawFaceIndex >= mesh.FaceReadInfos.Count)
                    continue;
                var acceptedFaceIndex = mesh.FaceReadInfos[rawFaceIndex].AcceptedFaceIndex;
                if (acceptedFaceIndex is not { } faceIndex || faceIndex >= mesh.Faces.Count)
                    continue;

                mesh.Faces[faceIndex].ApplyTextureWibble(new PsxTextureWibble
                {
                    UVelocity = uVelocity,
                    VVelocity = vVelocity,
                    Frequency = frequency,
                    ZeroUAmplitudes = zeroUAmplitudes,
                    ZeroVAmplitudes = zeroVAmplitudes,
                    Vertices = vertices,
                    // Spider-Man PC's v6 path ignores the two legacy base-UV
                    // bytes in each wibble vertex and starts from the face's
                    // widened u16/i16 coordinates instead. The shipped PC
                    // executable does this at 0x0047619F-0x00476204. Port
                    // assets use zero placeholders or redundant byte-range
                    // copies there, never values that differ from face UVs.
                    UsesFaceTextureCoordinates = version == 0x06
                });
            }

            stream.Seek(faceDataEnd, SeekOrigin.Begin);
        }
    }

    private static void ReadColourPulses(
        BinaryReader reader,
        long chunkEnd,
        Vector4[]? paletteToBake,
        List<PsxColourPulse> pulses)
    {
        var stream = reader.BaseStream;
        while (stream.Position + 4 <= chunkEnd)
        {
            var colourIndex = reader.ReadByte();
            var keyCount = reader.ReadByte();
            var initialKeyIndex = reader.ReadByte();
            var initialTimeAccumulator = reader.ReadByte();
            var keysEnd = stream.Position + (long)keyCount * 4;
            if (keyCount == 0 || keysEnd > chunkEnd)
                break;

            var keys = new PsxColourPulseKey[keyCount];
            for (var keyIndex = 0; keyIndex < keys.Length; keyIndex++)
            {
                keys[keyIndex] = new PsxColourPulseKey(
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte(),
                    reader.ReadByte());
            }

            pulses.Add(new PsxColourPulse
            {
                ColourIndex = colourIndex,
                InitialKeyIndex = initialKeyIndex,
                InitialTimeAccumulator = initialTimeAccumulator,
                Keys = keys
            });

            if (paletteToBake == null || colourIndex >= paletteToBake.Length)
                continue;
            var initial = EvaluateInitialPulse(keys, initialKeyIndex, initialTimeAccumulator);
            paletteToBake[colourIndex] = new Vector4(
                initial.X / 255f,
                initial.Y / 255f,
                initial.Z / 255f,
                1f);
        }
    }

    private static Vector3 EvaluateInitialPulse(
        PsxColourPulseKey[] keys,
        byte initialKeyIndex,
        byte initialTimeAccumulator)
    {
        var keyIndex = initialKeyIndex < keys.Length ? initialKeyIndex : 0;
        var time = (int)initialTimeAccumulator;
        for (var guard = 0; guard < 256; guard++)
        {
            var interval = keys[keyIndex].Interval;
            if (interval == 0 || time < interval)
                break;
            time -= interval;
            keyIndex = (keyIndex + 1) % keys.Length;
        }

        var current = keys[keyIndex];
        var next = keys[(keyIndex + 1) % keys.Length];
        var amount = current.Interval == 0
            ? 0f
            : Math.Clamp(time / (float)current.Interval, 0f, 1f);
        return Vector3.Lerp(
            new Vector3(current.R, current.G, current.B),
            new Vector3(next.R, next.G, next.B),
            amount);
    }

    private static int ResolveObjectIndex(uint itemOffset, int objectCount)
    {
        if (itemOffset < ObjectTableOffset)
            return -1;
        var relative = itemOffset - ObjectTableOffset;
        if (relative % ObjectRecordSize != 0)
            return -1;
        var objectIndex = (int)(relative / ObjectRecordSize);
        return objectIndex < objectCount ? objectIndex : -1;
    }
}
