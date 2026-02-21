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
            // Scan DDM files — filter out _o.ddm (object companions)
            var ddmFiles = Directory.GetFiles(inputDir, "*.ddm",
                    new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })
                .Where(f => !Path.GetFileNameWithoutExtension(f)
                    .EndsWith("_o", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var file in ddmFiles)
            {
                var entry = ScanDdmFile(file);
                if (entry != null)
                    dispatcher.TryEnqueue(() => _items.Add(entry));
            }

            // Scan PSX files that have mesh data (skip files with companion DDMs)
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
            var dir = Path.GetDirectoryName(file)!;
            var stem = Path.GetFileNameWithoutExtension(file);

            // Detect PSX layout companion for placed level assembly
            var companionPsx = FindCompanionFile(dir, stem, ".psx");
            var companionObjectsDdm = companionPsx != null
                ? FindCompanionFile(dir, stem + "_o", ".ddm")
                : null;

            return new MeshFileEntry
            {
                FileName = Path.GetFileName(file),
                FilePath = file,
                Format = companionPsx != null ? "DDM (placed)" : "DDM",
                ObjectCount = ddm.Objects.Count,
                MeshCount = ddm.Objects.Count,
                CompanionPsxPath = companionPsx,
                CompanionObjectsDdmPath = companionObjectsDdm,
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
                        triangles = ConvertDdmFile(entry, outputDir, embedTextures);

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

    private static int ConvertDdmFile(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddm = DdmFile.Parse(entry.FilePath);
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var outputFile = Path.Combine(outputDir, ddmName + ".glb");

        // Load DDX textures from the same directory
        Dictionary<string, byte[]>? ddxTextures = null;
        if (embedTextures)
        {
            var ddxFile = FindCompanionFile(inputDir, ddmName, ".ddx");
            if (ddxFile != null)
                ddxTextures = DdxArchive.ReadAllEntries(ddxFile);
        }

        // Load .lit lights from the same directory
        List<LitLight>? lights = null;
        var litFile = FindCompanionFile(inputDir, ddmName, ".lit");
        if (litFile != null)
        {
            try { lights = LitFile.Parse(litFile); }
            catch { /* ignore parse errors */ }
        }

        return GltfWriter.WriteDdm(ddm, outputFile, null, ddmName, ddxTextures, lights);
    }

    private static int ConvertPlacedDdm(MeshFileEntry entry, string outputDir, bool embedTextures)
    {
        var ddmName = Path.GetFileNameWithoutExtension(entry.FileName);
        var inputDir = Path.GetDirectoryName(entry.FilePath)!;
        var ddxPath = embedTextures ? inputDir : null;

        // Find _o.psx companion for objects placement
        var objectsPsx = entry.CompanionObjectsDdmPath != null
            ? FindCompanionFile(inputDir, ddmName + "_o", ".psx")
            : null;

        var (levelTris, objTris) = GltfWriter.WritePlacedLevel(
            entry.FilePath, entry.CompanionPsxPath!,
            entry.CompanionObjectsDdmPath, objectsPsx,
            outputDir, ddmName, ddxPath);

        return levelTris + objTris;
    }
}
