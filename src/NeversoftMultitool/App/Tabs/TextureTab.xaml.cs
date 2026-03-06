using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats.Psx;
using NeversoftMultitool.Core.Formats.XbxScene;

namespace NeversoftMultitool;

public sealed partial class TextureTab : UserControl
{
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<PsxFileEntry> _parentFiles = [];
    private CancellationTokenSource? _cts;
    private string _inputDir = "";
    private string _outputDir = "";
    private CancellationTokenSource? _previewCts;
    private bool _sortAscending = true;
    private string _sortColumn = "";

    public TextureTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _inputDir = path;
        InputPathText.Text = _inputDir;

        _items.Clear();
        _parentFiles.Clear();
        var candidateFiles = Directory.GetFiles(_inputDir)
            .Where(IsTextureFile)
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Probe files for format support
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        var supported = new List<string>();
        foreach (var file in candidateFiles)
        {
            var probe = FormatProbe.ProbeTexture(file);
            if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                unsupported.Add(new(Path.GetFileName(file)!, probe.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(file);
        }

        if (unsupported.Count > 0)
        {
            var proceed = await ScanSummaryDialog.ShowIfNeeded(
                XamlRoot, supported.Count, unsupported);
            if (!proceed) return;
        }

        foreach (var file in supported)
        {
            var fileName = Path.GetFileName(file)!;
            var entry = new PsxFileEntry
            {
                FileName = fileName,
                Format = ClassifyFormat(fileName)
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();

        // Enumerate texture counts in background
        var inputDir = _inputDir;
        var entries = _parentFiles.ToList();
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                try
                {
                    var inputFile = Path.Combine(inputDir, entry.FileName);
                    var count = CountTextures(inputFile, entry.Format);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TextureCount = count;
                        entry.HasTextures = count > 0;
                    });
                }
                catch
                {
                    dispatcher.TryEnqueue(() => entry.HasTextures = false);
                }
            }
        });
    }

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _parentFiles.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ExtractButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: PsxFileEntry parent }) return;
        if (!parent.HasTextures) return;

        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            // Collapse: remove children after parent
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            // Expand: lazy-load children on first expand
            if (parent.CachedChildren == null)
            {
                var inputFile = Path.Combine(_inputDir, parent.FileName);
                try
                {
                    parent.CachedChildren = EnumerateChildren(inputFile, parent.FileName, parent.Format);
                }
                catch
                {
                    parent.CachedChildren = [];
                }
            }

            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();
        var createSubDirs = CreateSubDirsCheckbox.IsChecked == true;
        var writeDds = WriteDdsCheckbox.IsChecked == true;
        var writeMipAtlas = WriteMipAtlasCheckbox.IsChecked == true;

        // Reset state
        foreach (var file in _parentFiles)
        {
            file.TextureCount = 0;
            file.ExtractedCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ExtractButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ExtractionProgress.Visibility = Visibility.Visible;
        ExtractionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _parentFiles.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var inputDir = _inputDir;
        var outputDir = _outputDir;

        // Snapshot parent entries for parallel iteration
        var entries = _parentFiles.ToList();

        await Task.Run(() =>
        {
            var semaphore = new SemaphoreSlim(Environment.ProcessorCount);

            var tasks = entries.Select((entry, index) => Task.Run(async () =>
            {
                await semaphore.WaitAsync(token);
                try
                {
                    if (token.IsCancellationRequested) return;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var inputFile = Path.Combine(inputDir, entry.FileName);
                    var (totalTex, writtenTex, skipped, success) =
                        ExtractTextures(inputFile, entry, outputDir, createSubDirs, writeDds, writeMipAtlas);

                    dispatcher.TryEnqueue(() =>
                    {
                        entry.TextureCount = totalTex;
                        entry.ExtractedCount = writtenTex;
                        entry.Status = (skipped, success) switch
                        {
                            (true, _) => ExtractionStatus.Skipped,
                            (_, true) => ExtractionStatus.Done,
                            _ => ExtractionStatus.Error
                        };

                        filesProcessed++;
                        ExtractionProgress.Value = (double)filesProcessed / totalFiles * 100;
                    });
                }
                finally
                {
                    semaphore.Release();
                }
            }, token)).ToArray();

            Task.WaitAll(tasks, token);
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);

        stopwatch.Stop();
        ExtractionProgress.Value = 100;
        CancelButton.Visibility = Visibility.Collapsed;
        ExtractButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus($"Completed in {stopwatch.Elapsed.TotalSeconds:F2}s");
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ExtractButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Extraction cancelled");
    }

    private void SortByFileName_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("FileName", f => f.FileName);
    }

    private void SortByTextures_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Textures", f => f.TextureCount);
    }

    private void SortByExtracted_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Extracted", f => f.ExtractedCount);
    }

    private void SortByStatus_Click(object sender, RoutedEventArgs e)
    {
        SortFiles("Status", f => (int)f.Status);
    }

    private void SortFiles<T>(string column, Func<PsxFileEntry, T> keySelector)
    {
        if (_parentFiles.Count == 0) return;

        if (_sortColumn == column)
            _sortAscending = !_sortAscending;
        else
        {
            _sortColumn = column;
            _sortAscending = true;
        }

        // Collapse all before sorting
        foreach (var parent in _parentFiles)
            parent.IsExpanded = false;

        var sorted = _sortAscending
            ? _parentFiles.OrderBy(keySelector).ToList()
            : _parentFiles.OrderByDescending(keySelector).ToList();

        _parentFiles.Clear();
        _parentFiles.AddRange(sorted);

        _items.Clear();
        foreach (var item in sorted)
            _items.Add(item);

        UpdateSortIcons();
    }

    private void UpdateSortIcons()
    {
        var glyph = _sortAscending ? "\uE70E" : "\uE70D";
        FileNameSortIcon.Glyph = _sortColumn == "FileName" ? glyph : "";
        TexturesSortIcon.Glyph = _sortColumn == "Textures" ? glyph : "";
        ExtractedSortIcon.Glyph = _sortColumn == "Extracted" ? glyph : "";
        StatusSortIcon.Glyph = _sortColumn == "Status" ? glyph : "";
    }

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FilesListView.SelectedItem is PsxTextureEntry texture)
            await LoadTexturePreview(texture);
        else
            ClearPreview();
    }

    private async Task LoadTexturePreview(PsxTextureEntry texture)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(280);
        TexturePreview.Source = null;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";

        var inputFile = Path.Combine(_inputDir, texture.ParentFileName);
        var nameHash = texture.NameHash;
        var format = ClassifyFormat(texture.ParentFileName);

        var result = await Task.Run(() => GetPreviewRgba(inputFile, nameHash, format), cts.Token);

        if (cts.Token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;

        if (result != null)
        {
            var (rgba, width, height) = result.Value;
            TexturePreview.Source = BitmapHelper.CreateFromRgba(width, height, rgba);
            PreviewDimensionsText.Text = $"{width} x {height}";
            PreviewInfoText.Text = $"{texture.PaletteType}\n{texture.NameDisplay}";
        }
        else
        {
            NoPreviewIcon.Visibility = Visibility.Visible;
            PreviewDimensionsText.Text = "Failed to decode";
        }
    }

    private void ClearPreview()
    {
        _previewCts?.Cancel();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        TexturePreview.Source = null;
        PreviewLoading.IsActive = false;
        NoPreviewIcon.Visibility = Visibility.Collapsed;
        PreviewDimensionsText.Text = "";
        PreviewInfoText.Text = "";
    }

    // ── Format classification ────────────────────────────────────────────

    private static bool IsTextureFile(string path)
    {
        var name = Path.GetFileName(path).ToLowerInvariant();

        // Multi-part extensions first
        if (name.EndsWith(".tex.xbx") || name.EndsWith(".img.xbx") ||
            name.EndsWith(".tex.wpc") || name.EndsWith(".img.wpc") ||
            name.EndsWith(".tex.ps2") || name.EndsWith(".img.ps2"))
            return true;

        var ext = Path.GetExtension(path);
        return ext.Equals(".psx", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".pvr", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".tex", StringComparison.OrdinalIgnoreCase)
               || ext.Equals(".img", StringComparison.OrdinalIgnoreCase);
    }

    private static TextureFileFormat ClassifyFormat(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.EndsWith(".tex.xbx") || lower.EndsWith(".tex.wpc"))
            return TextureFileFormat.XbxTex;
        if (lower.EndsWith(".img.xbx") || lower.EndsWith(".img.wpc"))
            return TextureFileFormat.XbxImg;
        if (lower.EndsWith(".tex.ps2") || lower.EndsWith(".img.ps2") ||
            lower.EndsWith(".tex") || lower.EndsWith(".img"))
            return TextureFileFormat.Ps2Tex;
        if (lower.EndsWith(".pvr"))
            return TextureFileFormat.Pvr;

        return TextureFileFormat.Psx;
    }

    // ── Format-routed operations ─────────────────────────────────────────

    private static int CountTextures(string inputFile, TextureFileFormat format)
    {
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
            {
                var result = Ps2TexFile.Parse(inputFile);
                return result.Success ? result.Textures.Count(t => t.Pixels != null) : 0;
            }
            case TextureFileFormat.XbxTex:
            {
                var result = XbxTexFile.Parse(inputFile);
                if (!result.Success) result = ThawTexFile.Parse(inputFile);
                return result.Success ? result.Textures.Count(t => t.Pixels != null) : 0;
            }
            case TextureFileFormat.XbxImg:
            {
                var result = XbxImgFile.Parse(inputFile);
                return result.Success ? result.Textures.Count(t => t.Pixels != null) : 0;
            }
            case TextureFileFormat.Pvr:
            {
                var pvr = PvrFileDecoder.DecodeToRgba(inputFile);
                return pvr != null ? 1 : 0;
            }
            default: // Psx
                return PsxLibrary.EnumerateTextures(inputFile).Count;
        }
    }

    private static List<PsxTextureEntry> EnumerateChildren(
        string inputFile, string parentFileName, TextureFileFormat format)
    {
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
            {
                var result = Ps2TexFile.Parse(inputFile);
                return result.Success
                    ? result.Textures.Where(t => t.Pixels != null).Select((t, i) => new PsxTextureEntry
                    {
                        ParentFileName = parentFileName,
                        NameHash = t.Checksum,
                        Width = t.Width,
                        Height = t.Height,
                        PaletteType = Ps2TexFile.DescribePsm(t.Psm),
                        Index = i,
                        ResolvedName = t.Name ?? QbKey.TryResolve(t.Checksum)
                    }).ToList()
                    : [];
            }
            case TextureFileFormat.XbxTex:
            case TextureFileFormat.XbxImg:
            {
                Ps2TexResult result;
                if (format == TextureFileFormat.XbxImg)
                {
                    result = XbxImgFile.Parse(inputFile);
                }
                else
                {
                    result = XbxTexFile.Parse(inputFile);
                    if (!result.Success) result = ThawTexFile.Parse(inputFile);
                }

                return result.Success
                    ? result.Textures.Where(t => t.Pixels != null).Select((t, i) => new PsxTextureEntry
                    {
                        ParentFileName = parentFileName,
                        NameHash = t.Checksum,
                        Width = t.Width,
                        Height = t.Height,
                        PaletteType = format == TextureFileFormat.XbxImg ? "Xbox IMG" : "Xbox TEX",
                        Index = i,
                        ResolvedName = t.Name ?? QbKey.TryResolve(t.Checksum)
                    }).ToList()
                    : [];
            }
            case TextureFileFormat.Pvr:
            {
                var pvr = PvrFileDecoder.DecodeToRgba(inputFile);
                return pvr != null
                    ?
                    [
                        new PsxTextureEntry
                        {
                            ParentFileName = parentFileName,
                            NameHash = 0,
                            Width = pvr.Value.Width,
                            Height = pvr.Value.Height,
                            PaletteType = "PVR",
                            Index = 0,
                            ResolvedName = Path.GetFileNameWithoutExtension(parentFileName)
                        }
                    ]
                    : [];
            }
            default: // Psx
            {
                var textures = PsxLibrary.EnumerateTextures(inputFile);
                return textures.Select((t, i) => new PsxTextureEntry
                {
                    ParentFileName = parentFileName,
                    NameHash = t.NameHash,
                    Width = t.Header.Width,
                    Height = t.Header.Height,
                    PaletteType = PsxLibrary.DescribePaletteType(t.Header),
                    Index = i,
                    ResolvedName = QbKey.TryResolve(t.NameHash)
                }).ToList();
            }
        }
    }

    private static (int totalTex, int writtenTex, bool skipped, bool success) ExtractTextures(
        string inputFile, PsxFileEntry entry, string outputDir,
        bool createSubDirs, bool writeDds, bool writeMipAtlas)
    {
        var stem = StripCompoundExtension(entry.FileName);

        switch (entry.Format)
        {
            case TextureFileFormat.Ps2Tex:
            {
                var ps2Result = Ps2TexFile.Parse(inputFile);
                if (!ps2Result.Success)
                    return (0, 0, false, false);

                var written = Ps2TexFile.SaveAllAsPng(ps2Result, outputDir, stem);
                return (ps2Result.Textures.Count, written, false, true);
            }
            case TextureFileFormat.XbxTex:
            {
                var result = XbxTexFile.Parse(inputFile);
                if (!result.Success) result = ThawTexFile.Parse(inputFile);
                if (!result.Success)
                    return (0, 0, false, false);

                var written = XbxTexFile.SaveAllAsPng(result, outputDir, stem);
                return (result.Textures.Count, written, false, true);
            }
            case TextureFileFormat.XbxImg:
            {
                var result = XbxImgFile.Parse(inputFile);
                if (!result.Success)
                    return (0, 0, false, false);

                var outPath = createSubDirs
                    ? Path.Combine(outputDir, stem, stem + ".png")
                    : Path.Combine(outputDir, stem + ".png");
                if (createSubDirs)
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var written = XbxImgFile.SaveAsPng(result, outPath);
                return (1, written, false, true);
            }
            case TextureFileFormat.Pvr:
            {
                var outPath = createSubDirs
                    ? Path.Combine(outputDir, stem, stem + ".png")
                    : Path.Combine(outputDir, stem + ".png");
                if (createSubDirs)
                    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                using var stream = File.OpenRead(inputFile);
                using var reader = new BinaryReader(stream);
                var ok = PvrFileDecoder.DecodeToPng(reader, 0, outPath);
                return (1, ok ? 1 : 0, false, ok);
            }
            default: // Psx
            {
                var result = PsxLibrary.ExtractTextures(inputFile, outputDir, createSubDirs, writeDds,
                    writeMipAtlas);
                return (result.TotalTextures, result.TexturesWritten, result.Skipped, result.Success);
            }
        }
    }

    private static (byte[] rgba, int width, int height)? GetPreviewRgba(
        string inputFile, uint nameHash, TextureFileFormat format)
    {
        switch (format)
        {
            case TextureFileFormat.Ps2Tex:
            {
                var ps2Result = Ps2TexFile.Parse(inputFile);
                if (!ps2Result.Success) return null;
                var tex = ps2Result.Textures.FirstOrDefault(t => t.Checksum == nameHash && t.Pixels != null);
                return tex?.Pixels != null ? (tex.Pixels, tex.Width, tex.Height) : null;
            }
            case TextureFileFormat.XbxTex:
            {
                var result = XbxTexFile.Parse(inputFile);
                if (!result.Success) result = ThawTexFile.Parse(inputFile);
                if (!result.Success) return null;
                var tex = result.Textures.FirstOrDefault(t => t.Checksum == nameHash && t.Pixels != null);
                return tex?.Pixels != null ? (tex.Pixels, tex.Width, tex.Height) : null;
            }
            case TextureFileFormat.XbxImg:
            {
                var result = XbxImgFile.Parse(inputFile);
                if (!result.Success) return null;
                var tex = result.Textures.FirstOrDefault(t => t.Pixels != null);
                return tex?.Pixels != null ? (tex.Pixels, tex.Width, tex.Height) : null;
            }
            case TextureFileFormat.Pvr:
                return PvrFileDecoder.DecodeToRgba(inputFile);
            default: // Psx
                return PsxLibrary.ExtractTextureByHash(inputFile, nameHash);
        }
    }

    private static string StripCompoundExtension(string filename)
    {
        string[] compoundExts =
        [
            ".tex.xbx", ".img.xbx", ".tex.wpc", ".img.wpc",
            ".tex.ps2", ".img.ps2"
        ];
        foreach (var ext in compoundExts)
        {
            if (filename.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                return filename[..^ext.Length];
        }

        return Path.GetFileNameWithoutExtension(filename);
    }
}
