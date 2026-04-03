#r "src/NeversoftMultitool/bin/Debug/net10.0/NeversoftMultitool.dll"

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NeversoftMultitool.Core.Formats.Ps2Scene;
using NeversoftMultitool.Core.Formats.Psx;

static void WriteBmp(string path, int width, int height, byte[] rgbaPixels)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);

    var rowStride = width * 4;
    var pixelDataSize = rowStride * height;
    var fileSize = 14 + 40 + pixelDataSize;

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((byte)'B');
    writer.Write((byte)'M');
    writer.Write(fileSize);
    writer.Write(0);
    writer.Write(14 + 40);

    writer.Write(40);
    writer.Write(width);
    writer.Write(height);
    writer.Write((short)1);
    writer.Write((short)32);
    writer.Write(0);
    writer.Write(pixelDataSize);
    writer.Write(2835);
    writer.Write(2835);
    writer.Write(0);
    writer.Write(0);

    for (var y = height - 1; y >= 0; y--)
    {
        var rowOffset = y * rowStride;
        for (var x = 0; x < width; x++)
        {
            var pixelOffset = rowOffset + x * 4;
            var r = rgbaPixels[pixelOffset + 0];
            var g = rgbaPixels[pixelOffset + 1];
            var b = rgbaPixels[pixelOffset + 2];
            var a = rgbaPixels[pixelOffset + 3];
            writer.Write(b);
            writer.Write(g);
            writer.Write(r);
            writer.Write(a);
        }
    }
}

if (Args.Count < 2)
{
    Console.Error.WriteLine("usage: dotnet script inspect_zone_header.csx <tex-file> <checksum> [<checksum>...]");
    Environment.Exit(1);
}

var texPath = Args[0];
var targets = Args.Skip(1)
    .Select(value => Convert.ToUInt32(value, 16))
    .ToHashSet();

var data = File.ReadAllBytes(texPath);
var entries = ThawZoneTexFile.ParseHeaderEntries(data);
var uploads = ThawZoneTexFile.Parse(data).Uploads;
var outputDir = Path.Combine(
    Path.GetDirectoryName(Path.GetFullPath(texPath)) ?? ".",
    "_inspect");
Directory.CreateDirectory(outputDir);

foreach (var entry in entries.Where(e => targets.Contains(e.Checksum)))
{
    var tex0 = entry.Tex0;
    var psm = (uint)((tex0 >> 20) & 0x3F);
    var tbw = (uint)((tex0 >> 14) & 0x3F);
    var tw = 1 << (int)((tex0 >> 26) & 0xF);
    var th = 1 << (int)((tex0 >> 30) & 0xF);
    var cbp = (uint)((tex0 >> 37) & 0x3FFF);
    var cpsm = (uint)((tex0 >> 51) & 0xF);
    Console.WriteLine($"Checksum=0x{entry.Checksum:X8}");
    Console.WriteLine($"  Size={tw}x{th}");
    Console.WriteLine($"  PSM=0x{psm:X} TBW=0x{tbw:X} CBP=0x{cbp:X} CPSM=0x{cpsm:X}");
    Console.WriteLine($"  Layout=0x{entry.LayoutMode:X8} DataOffset=0x{entry.DataOffset:X} UploadOffset=0x{entry.UploadOffset:X}");
    Console.WriteLine($"  DataSize=0x{entry.DataSize:X} PaletteBytes=0x{entry.PaletteBytes:X} BasePixelBytes=0x{entry.BasePixelBytes:X} Mips={entry.MipLevelCount}");

    var matchedUpload = uploads
        .Select((upload, index) => (upload, index))
        .FirstOrDefault(item =>
            item.upload.SourceDataOffset == entry.UploadOffset ||
            item.upload.RelativeDataOffset == entry.UploadOffset);
    if (matchedUpload.upload.PixelData != null)
    {
        Console.WriteLine(
            $"  Upload[{matchedUpload.index}] DBP=0x{matchedUpload.upload.Dbp:X} DBW=0x{matchedUpload.upload.Dbw:X} DPSM=0x{matchedUpload.upload.Dpsm:X} Size={matchedUpload.upload.Width}x{matchedUpload.upload.Height} RelOff=0x{matchedUpload.upload.RelativeDataOffset:X} SrcOff=0x{matchedUpload.upload.SourceDataOffset:X}");
    }

    var singleEntry = new[] { entry };
    var prepared = ThawZoneTexFile.DecodeFromHeaderEntries(data, uploads, singleEntry).FirstOrDefault();
    var upload = ThawZoneTexFile.DecodeFromHeaderEntries(uploads, singleEntry).FirstOrDefault();

    if (prepared?.Pixels != null)
    {
        var path = Path.Combine(outputDir, $"{entry.Checksum:X8}_prepared.bmp");
        WriteBmp(path, prepared.Width, prepared.Height, prepared.Pixels);
        Console.WriteLine($"  PreparedSHA256={Convert.ToHexString(SHA256.HashData(prepared.Pixels))}");
        Console.WriteLine($"  PreparedBmp={path}");
    }

    if (upload?.Pixels != null)
    {
        var path = Path.Combine(outputDir, $"{entry.Checksum:X8}_upload.bmp");
        WriteBmp(path, upload.Width, upload.Height, upload.Pixels);
        Console.WriteLine($"  UploadSHA256={Convert.ToHexString(SHA256.HashData(upload.Pixels))}");
        Console.WriteLine($"  UploadBmp={path}");
    }
}
