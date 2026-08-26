using System.Buffers.Binary;

namespace NeversoftMultitool.Core.Formats.Gba;

/// <summary>
///     The THPS2 GBA <b>3D skater model</b> — the realtime software-rendered
///     character (there are no sprite frames; the engine transforms and rasterizes
///     this model into a 64×64 sprite every frame). It is a <b>morph-target</b>
///     model: no skeleton — every animation frame stores the complete posed vertex
///     set, and the pose pool occupies ~3.9 MB, nearly half the cartridge. One mesh
///     is <b>shared by all 15 characters</b>; a character contributes only colours
///     (its outfit palette ramps) and the material→ramp binding.
///
///     <para><b>Layout</b> (every structure located by content, then closed by
///     arithmetic that cannot hold by accident):</para>
///     <list type="bullet">
///         <item><b>Model header</b> (32 B): <c>{u32 frameStride; u8 vertCounts[8];
///         u8 normCounts[8]; u8 faceCounts[8]; u32 facePtr}</c>. Found by scanning
///         for the exact identity <c>frameStride == 4 + Σ ceil(v/4)·12 +
///         Σ ceil(n/2)·4</c> with an in-ROM face pointer — the engine's own bind
///         function builds per-sub-object pointers with precisely those paddings.
///         THPS2: stride 864, 8 sub-objects (sub 4 = the 99-vertex body, sub 6 =
///         the 26-vertex deck), 172 vertices, 266 faces.</item>
///         <item><b>Clip table</b> directly after the header: <c>{u16 tickStart,
///         u16 tickCount}</c> entries, and a <b>tick→frame</b> u16 remap directly
///         after that, ending exactly at <c>facePtr</c> — the clip/tick boundary is
///         solved from that closure (the remap length must equal the maximum
///         <c>tickStart+tickCount</c>). THPS2: 221 clips, 7,874 ticks, 4,772
///         distinct frames.</item>
///         <item><b>Face bank</b> at <c>facePtr</c>: 8-byte records
///         <c>{v0,v1,v2, n0,n1,n2, u16 material|flags}</c>, vertex/normal indices
///         sub-object-local, sub-objects consecutive.</item>
///         <item><b>Frame pool</b>: <c>frameCount × frameStride</c>, ending exactly
///         at the first character asset (character 0's binding table) — which is how
///         its base is recovered. A frame = 3 signed anchor bytes + pad, then
///         per-sub-object s8 (x,y,z) triples in 12-byte-aligned blocks, then packed
///         u16 normals (encoding not yet decoded; unused here).</item>
///         <item><b>Characters</b>: name-first records of stride 0x4C
///         (<c>+0x00</c> name, <c>+0x40</c> outfit binding table — 8 outfits × 48 B
///         of material→ramp values, <c>+0x44</c> colour stream — 8 × 312 B palette
///         blocks, 13 ramps × 12 shades each). A face's material selects a ramp via
///         the outfit row (<c>paletteIndex = 2·rowValue − 100</c> into the block);
///         runtime lighting picks the shade within the ramp — this export takes a
///         fixed mid-ramp shade per material (verified: it colours the skater
///         anatomically — skin, shirt with logo, pants, shoes, deck).</item>
///     </list>
/// </summary>
public static class GbaSkaterModel
{
    private const uint RomBase = 0x08000000;
    public const int SubObjectCount = 8;
    private const int FaceRecordSize = 8;
    private const int CharacterStride = 0x4C;

    public sealed record ModelInfo(
        int HeaderOffset,
        int FrameStride,
        byte[] VertCounts,
        byte[] NormCounts,
        byte[] FaceCounts,
        int FaceBankOffset,
        int ClipCount,
        int ClipTableOffset,
        int TickTableOffset,
        int TickCount,
        int FrameCount,
        int FramePoolOffset,
        int CharacterTableOffset,
        int CharacterCount);

    public readonly record struct Face(int SubObject, int V0, int V1, int V2, int Material, bool SpecialFlag);

    public readonly record struct Clip(int Index, int TickStart, int TickCount);

    /// <summary>
    ///     Locates and cross-validates the whole model complex, or null when this ROM
    ///     doesn't carry it (only THPS2 GBA does).
    /// </summary>
    public static ModelInfo? TryLocate(ReadOnlySpan<byte> rom)
    {
        // The header identity admits sibling mesh headers too (THPS2 carries a
        // second, clipless one at 0x744C98 — likely a level object), so candidates
        // are walked until one closes as the full skater complex: clips + tick
        // remap + character table + frame pool.
        for (var header = FindHeader(rom, 0); header >= 0; header = FindHeader(rom, header + 4))
        {
            var model = TryCloseComplex(rom, header);
            if (model != null)
                return model;
        }

        return null;
    }

    private static ModelInfo? TryCloseComplex(ReadOnlySpan<byte> rom, int header)
    {
        var frameStride = (int)ReadU32(rom, header);
        var vertCounts = rom.Slice(header + 4, 8).ToArray();
        var normCounts = rom.Slice(header + 12, 8).ToArray();
        var faceCounts = rom.Slice(header + 20, 8).ToArray();
        var faceBank = (int)(ReadU32(rom, header + 28) - RomBase);

        // Clip table starts right after the header; the tick table follows it and
        // must end exactly at the face bank with length == max(tickStart+tickCount).
        var clipTable = header + 32;
        var found = false;
        int clipCount = 0, tickTable = 0, tickCount = 0;
        for (var boundary = clipTable + 4; boundary < faceBank; boundary += 4)
        {
            var clips = (boundary - clipTable) / 4;
            var maxEnd = 0;
            for (var i = 0; i < clips; i++)
            {
                int start = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(clipTable + i * 4, 2));
                int count = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(clipTable + i * 4 + 2, 2));
                maxEnd = Math.Max(maxEnd, start + count);
            }

            if ((faceBank - boundary) / 2 == maxEnd && (faceBank - boundary) % 2 == 0)
            {
                clipCount = clips;
                tickTable = boundary;
                tickCount = maxEnd;
                found = true;
                break;
            }
        }

        if (!found)
            return null;

        // Frame count = highest physical frame the tick remap references, plus one.
        var frameCount = 0;
        for (var i = 0; i < tickCount; i++)
            frameCount = Math.Max(frameCount,
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(tickTable + i * 2, 2)) + 1);

        // The character table gives the pool's END: character 0's binding table is
        // the first asset after the pool. Reuse the sprite-art content scan (whose
        // base is +0x44 into the true name-first records).
        var (spriteBase, characters) = FindCharacterTableBySpriteScan(rom);
        if (characters == 0)
            return null;
        var trueCharTable = spriteBase - 0x44;
        var poolEnd = (int)(ReadU32(rom, trueCharTable + 0x40) - RomBase);
        var framePool = poolEnd - frameCount * frameStride;
        if (framePool < 0 || framePool + (long)frameCount * frameStride > rom.Length)
            return null;

        return new ModelInfo(
            header, frameStride, vertCounts, normCounts, faceCounts, faceBank,
            clipCount, clipTable, tickTable, tickCount, frameCount, framePool,
            trueCharTable, characters);
    }

    /// <summary>All face records, sub-objects consecutive in header order.</summary>
    public static List<Face> ReadFaces(ReadOnlySpan<byte> rom, ModelInfo model)
    {
        var faces = new List<Face>();
        var offset = model.FaceBankOffset;
        for (var sub = 0; sub < SubObjectCount; sub++)
        {
            for (var i = 0; i < model.FaceCounts[sub]; i++)
            {
                var material = BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(offset + 6, 2));
                faces.Add(new Face(
                    sub, rom[offset], rom[offset + 1], rom[offset + 2],
                    material & 0x7F, (material & 0x80) != 0));
                offset += FaceRecordSize;
            }
        }

        return faces;
    }

    /// <summary>One frame's posed vertices, per sub-object, in model space (s8, z-up).</summary>
    public static sbyte[][][] ReadFrameVertices(ReadOnlySpan<byte> rom, ModelInfo model, int frame)
    {
        var result = new sbyte[SubObjectCount][][];
        var offset = model.FramePoolOffset + frame * model.FrameStride + 4; // skip anchor+pad
        for (var sub = 0; sub < SubObjectCount; sub++)
        {
            var count = model.VertCounts[sub];
            var verts = new sbyte[count][];
            for (var i = 0; i < count; i++)
                verts[i] = [(sbyte)rom[offset + i * 3], (sbyte)rom[offset + i * 3 + 1], (sbyte)rom[offset + i * 3 + 2]];
            result[sub] = verts;
            offset += (count + 3) / 4 * 12; // the engine's 12-byte-aligned block stride
        }

        return result;
    }

    public static List<Clip> ReadClips(ReadOnlySpan<byte> rom, ModelInfo model)
    {
        var clips = new List<Clip>(model.ClipCount);
        for (var i = 0; i < model.ClipCount; i++)
            clips.Add(new Clip(
                i,
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(model.ClipTableOffset + i * 4, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(model.ClipTableOffset + i * 4 + 2, 2))));
        return clips;
    }

    /// <summary>The physical frame a clip tick plays (through the tick→frame remap).</summary>
    public static int FrameForTick(ReadOnlySpan<byte> rom, ModelInfo model, int tick) =>
        BinaryPrimitives.ReadUInt16LittleEndian(rom.Slice(model.TickTableOffset + tick * 2, 2));

    /// <summary>
    ///     A character's flat per-material RGBA colours for one outfit: material →
    ///     the mid shade of its palette ramp (runtime lighting normally sweeps the
    ///     ramp; a fixed mid shade renders the skater's authored colours).
    /// </summary>
    public static byte[][]? TryGetMaterialColors(
        ReadOnlySpan<byte> rom, ModelInfo model, int character, int outfit)
    {
        if (character < 0 || character >= model.CharacterCount || outfit is < 0 or > 7)
            return null;
        var record = model.CharacterTableOffset + character * CharacterStride;
        var bindingPtr = ReadU32(rom, record + 0x40);
        var colourPtr = ReadU32(rom, record + 0x44);
        if (bindingPtr < RomBase || colourPtr < RomBase)
            return null;
        if (!GbaBiosLz77.TryDecompress(rom, (int)(colourPtr - RomBase), out var colours, out _)
            || colours.Length != 2496)
            return null;

        var row = (int)(bindingPtr - RomBase) + outfit * 48;
        var block = colours.AsSpan(outfit * 312, 312);
        var result = new byte[46][];
        for (var material = 0; material < 46; material++)
        {
            // rowValue v → OBJ palette entry base 2v (entries 100..255 live in the
            // 312-byte block at (entry−100)*2); mid shade = base + 6 of 12.
            var paletteEntry = Math.Clamp(2 * rom[row + material] + 6, 100, 255);
            var at = (paletteEntry - 100) * 2;
            var c = block[at] | (block[at + 1] << 8);
            result[material] =
            [
                Expand5(c & 0x1F), Expand5((c >> 5) & 0x1F), Expand5((c >> 10) & 0x1F), 0xFF
            ];
        }

        return result;
    }

    /// <summary>The character's name string (record +0x00), for display.</summary>
    public static string? TryGetCharacterName(ReadOnlySpan<byte> rom, ModelInfo model, int character)
    {
        var record = model.CharacterTableOffset + character * CharacterStride;
        var address = ReadU32(rom, record);
        if (address < RomBase || address >= RomBase + (uint)rom.Length)
            return null;
        var start = (int)(address - RomBase);
        var end = start;
        while (end < rom.Length && rom[end] != 0 && end - start < 32)
        {
            if (rom[end] < 0x20 || rom[end] > 0x7E)
                return null;
            end++;
        }

        return end > start ? System.Text.Encoding.ASCII.GetString(rom[start..end]) : null;
    }

    // The header identity: frameStride == 4 + Σ ceil(v/4)*12 + Σ ceil(n/2)*4, with a
    // plausible stride, nonzero counts, and an in-ROM face pointer. The paddings are
    // the engine's own bind arithmetic, so a random 32-byte window cannot satisfy it
    // alongside the pointer constraint.
    private static int FindHeader(ReadOnlySpan<byte> rom, int from)
    {
        for (var offset = from; offset + 32 <= rom.Length; offset += 4)
        {
            var stride = ReadU32(rom, offset);
            if (stride is < 64 or > 4096)
                continue;
            var facePtr = ReadU32(rom, offset + 28);
            if (facePtr < RomBase || facePtr >= RomBase + (uint)rom.Length)
                continue;

            var vertBytes = 0;
            var normBytes = 0;
            var vertTotal = 0;
            for (var i = 0; i < 8; i++)
            {
                var v = rom[offset + 4 + i];
                var n = rom[offset + 12 + i];
                vertBytes += (v + 3) / 4 * 12;
                normBytes += (n + 1) / 2 * 4;
                vertTotal += v;
            }

            if (vertTotal < 32)
                continue;
            if (4 + vertBytes + normBytes == (int)stride)
                return offset;
        }

        return -1;
    }

    // GbaSpriteArt's character scan keys on the colour/portrait stream pair, which
    // sits at +0x44/+0x48 of the true name-first records — so the true table base is
    // that scan's base minus 0x44.
    private static (int SpriteScanBase, int Count) FindCharacterTableBySpriteScan(ReadOnlySpan<byte> rom)
    {
        for (var offset = 0x44; offset + 8 <= rom.Length; offset += 4)
        {
            if (!IsColourPortraitPair(rom, offset))
                continue;
            if (offset >= 0x44 + CharacterStride && IsColourPortraitPair(rom, offset - CharacterStride))
                continue;
            var count = 1;
            while (IsColourPortraitPair(rom, offset + count * CharacterStride))
                count++;
            if (count >= 8)
                return (offset, count);
        }

        return (0, 0);
    }

    private static bool IsColourPortraitPair(ReadOnlySpan<byte> rom, int offset)
    {
        if (offset + 8 > rom.Length)
            return false;
        var colour = ReadU32(rom, offset);
        var portrait = ReadU32(rom, offset + 4);
        if (colour < RomBase || colour >= RomBase + (uint)rom.Length)
            return false;
        if (portrait < RomBase || portrait >= RomBase + (uint)rom.Length)
            return false;
        return GbaBiosLz77.TryDecompress(rom, (int)(colour - RomBase), out var c, out _) && c.Length == 2496
               && GbaBiosLz77.TryDecompress(rom, (int)(portrait - RomBase), out var p, out _) && p.Length == 1024;
    }

    private static uint ReadU32(ReadOnlySpan<byte> rom, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(rom.Slice(offset, 4));

    private static byte Expand5(int v) => (byte)((v << 3) | (v >> 2));
}
