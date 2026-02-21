using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Archives;
using NeversoftMultitool.Core.Formats.Mesh;
using NeversoftMultitool.Core.Formats.Psx;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace NeversoftMultitool;

public sealed partial class MeshConverterTab : UserControl
{
    private readonly ObservableCollection<MeshFileEntry> _items = [];
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _cts;

    public MeshConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _inputDir = folder.Path;
        InputPathText.Text = _inputDir;

        _items.Clear();
        ConvertButton.IsEnabled = false;

        var inputDir = _inputDir;
        var dispatcher = DispatcherQueue;

        await Task.Run(() =>
        {
            // Scan DDM files (exclude _o companion files — they're processed with their level)
            var ddmFiles = Directory.GetFiles(inputDir, "*.ddm",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => !Path.GetFileNameWithoutExtension(f)
                    .EndsWith("_o", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in ddmFiles)
            {
                var entry = ScanDdmFile(file);
                if (entry != null)
                    dispatcher.TryEnqueue(() => _items.Add(entry));
            }

            // Scan PSX files that have mesh data (skip texture-only, skip files with companion DDMs)
            var ddmStems = ddmFiles
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var psxFiles = Directory.GetFiles(inputDir, "*.psx",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => !ddmStems.Contains(Path.GetFileNameWithoutExtension(f)))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var file in psxFiles)
            {
                var entry = ScanPsxFile(file);
                if (entry != null)
                    dispatcher.TryEnqueue(() => _items.Add(entry));
            }
        });

        UpdateUiState();
    }

    private static MeshFileEntry? ScanDdmFile(string file)
    {
        try
        {
            var ddm = DdmFile.Parse(file);
            var ddmName = Path.GetFileNameWithoutExtension(file);
            var inputDir = Path.GetDirectoryName(file)!;

            // Auto-resolve companion files from the same directory
            var psxFile = FindCompanionFile(inputDir, ddmName, ".psx");
            List<PsxObjectPosition>? positions = null;
            if (psxFile != null)
            {
                try { positions = PsxObjectPositionParser.ParsePositions(psxFile); }
                catch { /* no valid positions */ }
            }

            string? objectsDdmPath = null;
            if (positions != null)
                objectsDdmPath = FindCompanionFile(inputDir, ddmName + "_o", ".ddm");

            var totalObjects = ddm.Objects.Count;
            if (objectsDdmPath != null)
            {
                try
                {
                    var objectsDdm = DdmFile.Parse(objectsDdmPath);
                    totalObjects += objectsDdm.Objects.Count;
                }
                catch { /* ignore companion parse errors */ }
            }

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = positions != null ? "DDM (placed)" : "DDM",
                ObjectCount = totalObjects,
                MeshCount = ddm.Objects.Count,
                CompanionObjectsDdmPath = objectsDdmPath,
                CompanionPsxPath = psxFile,
            };
        }
        catch
        {
            return null;
        }
    }

    private static MeshFileEntry? ScanPsxFile(string file)
    {
        try
        {
            var psxFile = PsxMeshFile.Parse(file);
            if (psxFile == null) return null;

            // For level geometry files (*_g.psx), find the companion texture library (*_l.psx)
            string? companionLibPath = null;
            var stem = Path.GetFileNameWithoutExtension(file);
            if (stem.EndsWith("_g", StringComparison.OrdinalIgnoreCase))
            {
                var libStem = stem[..^2] + "_l";
                var dir = Path.GetDirectoryName(file)!;
                companionLibPath = FindCompanionFile(dir, libStem, ".psx");
            }

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = "PSX",
                ObjectCount = psxFile.Objects.Count,
                MeshCount = psxFile.Meshes.Count,
                CompanionLibraryPsxPath = companionLibPath,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FindCompanionFile(string directory, string stem, string extension)
    {
        var files = Directory.GetFiles(directory, stem + extension,
            new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive });
        return files.Length > 0 ? files[0] : null;
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        var hwnd = WindowNative.GetWindowHandle(MainWindow.Instance);
        InitializeWithWindow.Initialize(picker, hwnd);

        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        _outputDir = folder.Path;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _items.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _items)
        {
            file.TriangleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalTriangles = 0;
        var totalConverted = 0;
        var totalFiles = _items.Count;
        var dispatcher = DispatcherQueue;
        var outputDir = _outputDir;
        var embedTextures = EmbedTexturesCheckbox.IsChecked == true;
        var token = _cts.Token;

        var entries = _items.ToList();

        await Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested) break;

                dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                try
                {
                    int triangles;
                    if (entry.IsPsx)
                        triangles = ConvertPsxFile(entry, outputDir, embedTextures);
                    else if (entry.IsPlacedLevel)
                        triangles = ConvertPlacedDdm(entry, outputDir, embedTextures);
                    else
                        triangles = ConvertStandaloneDdm(entry, outputDir, embedTextures);

                    Interlocked.Add(ref totalTriangles, triangles);
                    Interlocked.Increment(ref totalConverted);

                    var processed = Interlocked.Increment(ref filesProcessed);
                    var tris = triangles;

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TriangleCount = tris;
                        entry.Status = ExtractionStatus.Done;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
                catch
                {
                    var processed = Interlocked.Increment(ref filesProcessed);
                    dispatcher.TryEnqueue(() =>
                    {
                        entry.Status = ExtractionStatus.Error;
                        ConversionProgress.Value = (double)processed / totalFiles * 100;
                    });
                }
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        ConversionProgress.Value = 100;
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus(
            $"Converted {totalConverted}/{totalFiles} files " +
            $"({totalTriangles:N0} triangles) in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    private static int ConvertPsxFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var psxFile = PsxMeshFile.Parse(entry.FilePath)
            ?? throw new InvalidOperationException("No mesh data");

        PsxGltfWriter.TextureProvider? textureProvider = null;
        if (embedTextures)
        {
            var filePath = entry.FilePath;
            var companionLibPath = entry.CompanionLibraryPsxPath;
            textureProvider = hash =>
            {
                var result = PsxLibrary.ExtractTextureByHash(filePath, hash);
                if (result == null && companionLibPath != null)
                    result = PsxLibrary.ExtractTextureByHash(companionLibPath, hash);
                if (result == null) return null;
                var (rgba, w, h) = result.Value;
                return ImageWriter.WritePngToMemory(w, h, rgba);
            };
        }

        var stem = Path.GetFileNameWithoutExtension(entry.FileName);
        var outputFile = Path.Combine(outputDir, stem + ".glb");
        return PsxGltfWriter.Write(psxFile, outputFile, textureProvider);
    }

    private static int ConvertStandaloneDdm(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var outputFile = Path.Combine(outputDir, ddmName + ".glb");
        var ddxTextures = embedTextures ? LoadDdxForEntry(entry.FilePath, ddmName) : null;
        return GltfWriter.WriteDdm(ddm, outputFile, null, ddmName, ddxTextures);
    }

    private static int ConvertPlacedDdm(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;

        var psxFile = PsxLayoutFile.Parse(entry.CompanionPsxPath!)
            ?? throw new InvalidOperationException("PSX file has no mesh data");

        var objectsDdm = entry.CompanionObjectsDdmPath != null
            ? DdmFile.Parse(entry.CompanionObjectsDdmPath)
            : null;

        PsxLayoutFile? objectsPsxFile = null;
        var objectsPsxPath = FindCompanionFile(inputDir, ddmName + "_o", ".psx");
        if (objectsPsxPath != null)
        {
            try { objectsPsxFile = PsxLayoutFile.Parse(objectsPsxPath); }
            catch { /* Objects PSX parse failed — objects placed at local origin */ }
        }

        // DDX archives and .lit lights live in the same directory as the DDM files
        var ddxPath = embedTextures ? inputDir : null;
        var result = GltfWriter.WritePlacedLevel(ddm, objectsDdm, psxFile, objectsPsxFile,
            outputDir, ddmName, null, ddxPath);
        return result.Combined;
    }

    /// <summary>
    /// Loads DDX textures for a standalone DDM entry from the same directory.
    /// </summary>
    private static Dictionary<string, byte[]>? LoadDdxForEntry(string filePath, string ddmName)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        var ddxFile = FindCompanionFile(dir, ddmName, ".ddx");
        return ddxFile != null ? DdxArchive.ReadAllEntries(ddxFile) : null;
    }
}
