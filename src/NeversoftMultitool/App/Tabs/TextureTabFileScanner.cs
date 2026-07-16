using System.Collections.Concurrent;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Archives;

namespace NeversoftMultitool;

/// <summary>
///     Off-thread discovery for the Texture tab. Folder scans probe candidate
///     files in parallel (the old UI-thread loop froze for minutes on 10k+
///     texture trees); archive scans enumerate entries INCLUDING nested
///     archives, so level textures that ship inside .pre entries of a WAD are
///     browsable. Returns plain lists — the caller bulk-adds on the UI thread.
/// </summary>
internal static class TextureTabFileScanner
{
    private static readonly string[] NestedArchiveSuffixes =
        [".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".pak", ".apk"];

    public sealed record ScanResult(
        List<PsxFileEntry> Supported,
        List<ScanSummaryDialog.UnsupportedFile> Unsupported);

    public static ScanResult ScanDirectory(string inputDir, IProgress<int>? progress, CancellationToken ct)
    {
        var candidates = Directory.EnumerateFiles(inputDir, "*", SearchOption.AllDirectories)
            .Where(TextureTabTextureOperations.IsTextureFile)
            .ToList();

        var supported = new ConcurrentBag<PsxFileEntry>();
        var unsupported = new ConcurrentBag<ScanSummaryDialog.UnsupportedFile>();
        var processed = 0;

        Parallel.ForEach(
            candidates,
            new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = Environment.ProcessorCount },
            file =>
            {
                var probe = FormatProbe.ProbeTexture(file);
                if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                {
                    unsupported.Add(new ScanSummaryDialog.UnsupportedFile(
                        Path.GetFileName(file)!, probe.UnsupportedReason ?? "Unknown format"));
                }
                else
                {
                    var fileName = Path.GetFileName(file)!;
                    supported.Add(new PsxFileEntry
                    {
                        FileName = fileName,
                        Source = new FileSystemAssetSource(file),
                        RelativePath = MakeRelativePath(file, inputDir),
                        Format = TextureTabTextureOperations.ClassifyFormat(fileName)
                    });
                }

                if (progress != null)
                    progress.Report(Interlocked.Increment(ref processed));
            });

        return new ScanResult(
            [.. supported.OrderBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)],
            [.. unsupported.OrderBy(file => file.FileName, StringComparer.OrdinalIgnoreCase)]);
    }

    public static ScanResult ScanArchive(string archivePath, IProgress<int>? progress, CancellationToken ct)
    {
        var backend = ArchiveAssetBackend.TryOpen(archivePath);
        if (backend == null)
            return new ScanResult([], []);

        // Breadth-first over nested archives: THPS3 level .tex dictionaries live
        // inside .pre entries of SKATE3.WAD; THAW zone textures inside pak
        // entries of DATAP.WAD.
        var entries = new List<PsxFileEntry>();
        var pending = new Queue<ArchiveAssetBackend>();
        pending.Enqueue(backend);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var current = pending.Dequeue();

            foreach (var archiveEntry in current.Entries)
            {
                ct.ThrowIfCancellationRequested();

                if (TextureTabTextureOperations.IsTextureFile(archiveEntry.Name))
                {
                    entries.Add(new PsxFileEntry
                    {
                        FileName = archiveEntry.Name,
                        Source = new ArchiveAssetSource(current, archiveEntry),
                        RelativePath = $"{current.DisplayPath}::{archiveEntry.FullName}",
                        Format = TextureTabTextureOperations.ClassifyFormat(archiveEntry.Name)
                    });

                    progress?.Report(entries.Count);
                    continue;
                }

                if (!OrdinalFileName.HasAnySuffix(archiveEntry.Name, NestedArchiveSuffixes) &&
                    !HasNestedDoubleExtension(archiveEntry.Name))
                {
                    continue;
                }

                var nested = current.TryOpenNested(archiveEntry);
                if (nested != null)
                    pending.Enqueue(nested);
            }
        }

        entries.Sort(static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left.RelativePath, right.RelativePath));
        return new ScanResult(entries, []);
    }

    /// <summary>Catches double extensions like .pak.ps2 / .apk.ngc that the plain suffix list misses.</summary>
    private static bool HasNestedDoubleExtension(string name)
    {
        var ext = ArchiveTypeDetector.GetArchiveExtension(name);
        return NestedArchiveSuffixes.Contains(ext);
    }

    private static string MakeRelativePath(string file, string rootDir)
    {
        if (string.IsNullOrEmpty(rootDir)) return Path.GetFileName(file);
        try
        {
            return Path.GetRelativePath(rootDir, file);
        }
        catch
        {
            return Path.GetFileName(file);
        }
    }
}
