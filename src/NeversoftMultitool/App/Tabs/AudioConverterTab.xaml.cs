using System.Collections.ObjectModel;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core;
using NeversoftMultitool.Core.Formats;
using NeversoftMultitool.Core.Formats.Audio;
using NeversoftMultitool.Core.Settings;

namespace NeversoftMultitool;

public sealed partial class AudioConverterTab : UserControl, IDisposable
{
    // .nds/.gob: the DS carts' sound effects ship as Nitro .swav waves inside the
    // GOB container, which is reachable only through the cart or its .gfc pair.
    private static readonly string[] ArchiveExtensions =
        [".ps2", ".pak", ".wad", ".pre", ".prx", ".pkr", ".nds", ".gob"];

    /// <summary>Entries worth opening as containers of their own during an archive scan.</summary>
    private static readonly string[] NestedArchiveSuffixes =
        [".pre", ".prx", ".prd", ".prf", ".prg", ".pkr", ".pak", ".apk", ".gob", ".sdat"];

    private readonly AudioConverterTabConversionController _conversionController = new();
    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<AudioFileEntry> _parentFiles = [];
    private readonly Dictionary<AudioPreviewCacheKey, string> _previewCache = [];

    // Temp file cache
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "AudioPreview");
    private string _inputDir = "";

    // Playback
    private readonly TimeSliderValueConverter _sliderTooltipConverter;
    private MediaPlayer? _mediaPlayer;
    private string _outputDir = "";
    private bool _outputManuallySet;
    private DispatcherTimer? _positionTimer;
    private CancellationTokenSource? _previewCts;
    private bool _suppressVolumeEvents;
    private bool _updatingSlider;
    private DebouncedAction? _persistVolume;
    private CancellationTokenSource? _metadataProbeCts;

    public AudioConverterTab()
    {
        InitializeComponent();
        FilesListView.ItemsSource = _items;
        Unloaded += AudioConverterTab_Unloaded;

        _sliderTooltipConverter = (TimeSliderValueConverter)Resources["PlaybackTooltipConverter"];

        // Applying the persisted volume fires ValueChanged; it must not write
        // the same value straight back (Changed listeners re-probe Blender).
        _suppressVolumeEvents = true;
        VolumeSlider.Value = UserSettings.PlayerVolume * 100;
        _suppressVolumeEvents = false;

        // One settings write per gesture: every UserSettings write fires the
        // global Changed event, whose listeners re-probe Blender and rebuild
        // the Meshes tab's animation list — per-tick writes stutter the drag.
        _persistVolume = new DebouncedAction(
            DispatcherQueue,
            TimeSpan.FromMilliseconds(400),
            () => UserSettings.PlayerVolume = VolumeSlider.Value / 100.0);

        // Reclaim preview WAVs left behind if a previous session crashed
        // before Dispose could clean the temp root.
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            /* another instance may hold a lock — ignore */
        }
    }

    public void Dispose()
    {
        Unloaded -= AudioConverterTab_Unloaded;
        CancelAudioMetadataProbe();
        ClearPreview();
        _conversionController.Dispose();

        // Preview WAVs accumulate in per-conversion GUID subdirectories.
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
            // A file may still be locked by a just-disposed MediaPlayer.
        }
    }

    private async void InputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        CancelAudioMetadataProbe();

        _inputDir = path;
        InputPathText.Text = _inputDir;
        DefaultOutputToInput(path);

        ClearPreview();
        _previewCache.Clear();

        _items.Clear();
        _parentFiles.Clear();
        var audioFiles = AudioConverterTabOperations.FindAudioFiles(_inputDir);

        // Probe audio files for unsupported variants (e.g. ADX encoding type)
        var unsupported = new List<ScanSummaryDialog.UnsupportedFile>();
        var supported = new List<string>();
        var cueSheets = new List<string>();
        foreach (var filePath in audioFiles)
        {
            // SFX is metadata owned by a same-directory KAT/VAB row. It must
            // not be probed or materialized as standalone audio.
            if (AudioConverterTabOperations.IsSfxCueSheet(filePath))
            {
                cueSheets.Add(filePath);
                continue;
            }

            var probe = FormatProbe.ProbeAudio(filePath);
            if (probe.Support == FormatProbe.FormatSupport.Unsupported)
                unsupported.Add(new ScanSummaryDialog.UnsupportedFile(Path.GetFileName(filePath)!,
                    probe.UnsupportedReason ?? "Unknown format"));
            else
                supported.Add(filePath);
        }

        if (unsupported.Count > 0)
        {
            var proceed = await ScanSummaryDialog.ShowIfNeeded(
                XamlRoot, supported.Count, unsupported);
            if (!proceed) return;
        }

        var scan = BuildAudioScan(
            supported.Concat(cueSheets).Select(filePath =>
            {
                var relativePath = MakeRelativePath(filePath, _inputDir);
                return new AudioScanAsset(
                    relativePath,
                    Path.GetFileName(filePath)!,
                    relativePath,
                    new FileInfo(filePath).Length,
                    new FileSystemAssetSource(filePath));
            }));
        foreach (var entry in scan.Entries)
        {
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        MainWindow.Instance?.SetStatus(FormatAudioScanStatus(null, scan));
        UpdateUiState();
        StartAudioMetadataProbe();
    }

    private async void SelectArchive_Click(object sender, RoutedEventArgs e)
    {
        var path = await FilePickerHelper.PickFileAsync(ArchiveExtensions);
        if (path == null) return;

        CancelAudioMetadataProbe();

        _inputDir = Path.GetDirectoryName(path) ?? "";
        InputPathText.Text = path;
        if (_inputDir.Length > 0)
            DefaultOutputToInput(_inputDir);

        ClearPreview();
        _previewCache.Clear();
        _items.Clear();
        _parentFiles.Clear();

        await Task.Run(() =>
        {
            var backend = ArchiveAssetBackend.TryOpen(path);
            if (backend == null)
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    MainWindow.Instance?.SetStatus($"{Path.GetFileName(path)}: unsupported archive.");
                    UpdateUiState();
                });
                return;
            }

            var archiveName = Path.GetFileName(path);
            var assets = new List<AudioScanAsset>();

            // Breadth-first over nested archives, like the Texture tab: a DS cart's
            // waves are not in the cart's own file table but inside the .gob it
            // carries, so walking only the top level lists nothing at all.
            var pending = new Queue<ArchiveAssetBackend>();
            pending.Enqueue(backend);
            while (pending.Count > 0)
            {
                var current = pending.Dequeue();
                foreach (var archiveEntry in current.Entries)
                {
                    if (AudioConverterTabOperations.IsAudioFile(archiveEntry.Name))
                    {
                        var entryPath = archiveEntry.FullName;
                        assets.Add(new AudioScanAsset(
                            entryPath,
                            EntryLeaf(entryPath),
                            $"{current.DisplayPath}::{entryPath}",
                            archiveEntry.Size,
                            new ArchiveAssetSource(current, archiveEntry)));
                        continue;
                    }

                    if (!OrdinalFileName.HasAnySuffix(archiveEntry.Name, NestedArchiveSuffixes))
                        continue;

                    var nested = current.TryOpenNested(archiveEntry);
                    if (nested != null)
                        pending.Enqueue(nested);
                }
            }

            var scan = BuildAudioScan(assets);

            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var entry in scan.Entries)
                {
                    _parentFiles.Add(entry);
                    _items.Add(entry);
                }

                MainWindow.Instance?.SetStatus(FormatAudioScanStatus(archiveName, scan));

                UpdateUiState();
                StartAudioMetadataProbe();
            });
        });
    }

    /// <summary>
    ///     Duration probes can walk or read an entire stream, so they run only
    ///     after the rows are visible. XA reuses that one byte read to discover
    ///     interleaved channel children and expandability as well.
    /// </summary>
    private void StartAudioMetadataProbe()
    {
        // A rescan replaces the entry list wholesale, so the previous probe
        // would keep reading the stale files to completion (and probes would
        // stack per rescan) — supersede it like the preview/conversion jobs.
        CancelAudioMetadataProbe();

        // A bank has no truthful aggregate timeline; its independent child
        // durations resolve lazily when the bank is expanded.
        var entries = _parentFiles
            .Where(static parent => parent.AudioFormat is not ("VAB" or "KAT"))
            .ToList();
        if (entries.Count == 0) return;

        var cts = new CancellationTokenSource();
        _metadataProbeCts = cts;
        var token = cts.Token;
        var dispatcher = DispatcherQueue;
        _ = Task.Run(() =>
        {
            foreach (var entry in entries)
            {
                if (token.IsCancellationRequested)
                    return;

                try
                {
                    var data = entry.Source.ReadBytes();
                    var duration = AudioDurationProbe.Probe(entry.AudioFormat, data);
                    var xaChildren = entry.AudioFormat == "XA"
                        ? AudioConverterTabOperations.EnumerateChildren(entry, data)
                        : null;

                    if (token.IsCancellationRequested)
                        return;

                    dispatcher.TryEnqueue(() =>
                    {
                        // A queued publication can outlive its scan or the tab.
                        if (token.IsCancellationRequested || !_parentFiles.Contains(entry))
                            return;

                        entry.DurationSeconds = duration;
                        if (xaChildren is not { Count: > 0 } || entry.CachedChildren != null)
                            return;

                        entry.CachedChildren = xaChildren;
                        entry.NotifyExpandabilityChanged();
                    });
                }
                catch
                {
                    // Unreadable or malformed leaves retain blank metadata and
                    // must not prevent later rows from being probed.
                }
            }
        }, token);
    }

    private void CancelAudioMetadataProbe()
    {
        _metadataProbeCts?.Cancel();
        _metadataProbeCts?.Dispose();
        _metadataProbeCts = null;
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

    private static AudioScanResult BuildAudioScan(IEnumerable<AudioScanAsset> input)
    {
        var assets = input
            .OrderBy(static asset => asset.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static asset => asset.RelativePath, StringComparer.Ordinal)
            .ToArray();
        var ownership = SfxExactStemOwnership.Plan(assets
            .Select(static (asset, index) => new SfxScanCandidate(
                index,
                asset.EntryPath,
                TryReadCueData(asset)))
            .ToArray());
        var sheetsByBank = new Dictionary<int, List<AudioCueSheetBinding>>();
        var exactPaired = 0;
        var aliasPaired = 0;
        var omitted = 0;

        foreach (var item in ownership)
        {
            if (item.BankIndex is not { } bankIndex ||
                item.CueIndex < 0 || item.CueIndex >= assets.Length ||
                bankIndex < 0 || bankIndex >= assets.Length)
            {
                omitted++;
                continue;
            }

            var cue = assets[item.CueIndex];
            if (!sheetsByBank.TryGetValue(bankIndex, out var sheets))
            {
                sheets = [];
                sheetsByBank.Add(bankIndex, sheets);
            }

            sheets.Add(new AudioCueSheetBinding(cue.FileName, cue.RelativePath, cue.Source));
            if (item.IsAlias)
                aliasPaired++;
            else
                exactPaired++;
        }

        var entries = new List<AudioFileEntry>();
        for (var index = 0; index < assets.Length; index++)
        {
            var asset = assets[index];
            if (AudioConverterTabOperations.IsSfxCueSheet(asset.FileName))
                continue;

            var extension = Path.GetExtension(asset.FileName).ToLowerInvariant();
            var cueSheets = sheetsByBank.TryGetValue(index, out var ownedSheets)
                ? ownedSheets
                    .OrderBy(static sheet => sheet.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static sheet => sheet.RelativePath, StringComparer.Ordinal)
                    .ToArray()
                : [];
            entries.Add(new AudioFileEntry
            {
                FileName = asset.FileName,
                AudioFormat = AudioConverterTabOperations.DetectFormat(extension),
                Source = asset.Source,
                RelativePath = asset.RelativePath,
                Size = asset.Size,
                CueSheets = cueSheets
            });
        }

        return new AudioScanResult(entries, exactPaired, aliasPaired, omitted);
    }

    private static byte[]? TryReadCueData(AudioScanAsset asset)
    {
        if (!AudioConverterTabOperations.IsSfxCueSheet(asset.FileName))
            return null;

        try
        {
            return asset.Source.ReadBytes();
        }
        catch
        {
            // Exact-stem ownership remains path-based so an unreadable sheet can
            // conservatively fall back to the raw bank. Cross-stem ownership,
            // which needs positive cue-layout evidence, simply omits it.
            return null;
        }
    }

    private static string FormatAudioScanStatus(string? containerName, AudioScanResult scan)
    {
        var location = string.IsNullOrEmpty(containerName) ? string.Empty : $" in {containerName}";
        var message = scan.Entries.Count == 0
            ? $"No audio entries found{location}."
            : $"Found {scan.Entries.Count} audio entrie(s){location}.";

        if (scan.ExactStemCueSheets > 0)
            message += $" Paired {scan.ExactStemCueSheets} exact-stem SFX cue sheet(s).";
        if (scan.AliasCueSheets > 0)
            message += $" Paired {scan.AliasCueSheets} high-confidence cross-stem SFX alias sheet(s).";
        if (scan.OmittedCueSheets > 0)
            message += $" Omitted {scan.OmittedCueSheets} unpaired or ambiguous SFX cue sheet(s).";
        return message;
    }

    private static string EntryLeaf(string entryPath)
    {
        var normalized = entryPath.Replace('\\', '/');
        var separator = normalized.LastIndexOf('/');
        return separator < 0 ? normalized : normalized[(separator + 1)..];
    }

    private sealed record AudioScanAsset(
        string EntryPath,
        string FileName,
        string RelativePath,
        long Size,
        AssetSource Source);

    private sealed record AudioScanResult(
        IReadOnlyList<AudioFileEntry> Entries,
        int ExactStemCueSheets,
        int AliasCueSheets,
        int OmittedCueSheets);

    private async void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = await FolderPickerHelper.PickFolderAsync();
        if (path == null) return;

        _outputDir = path;
        _outputManuallySet = true;
        OutputPathText.Text = _outputDir;
        UpdateUiState();
    }

    private void DefaultOutputToInput(string dir)
    {
        if (_outputManuallySet)
            return;

        _outputDir = dir;
        OutputPathText.Text = dir;
        UpdateUiState();
    }

    private void UpdateUiState()
    {
        var hasFiles = _parentFiles.Count > 0;
        var hasOutput = !string.IsNullOrEmpty(_outputDir);

        EmptyStatePanel.Visibility = hasFiles ? Visibility.Collapsed : Visibility.Visible;
        FileListCard.Visibility = hasFiles ? Visibility.Visible : Visibility.Collapsed;
        ConvertButton.IsEnabled = hasFiles && hasOutput;
    }

    private void ExpandCollapse_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AudioFileEntry parent } || !parent.IsExpandable) return;

        var parentIndex = _items.IndexOf(parent);
        if (parentIndex < 0) return;

        if (parent.IsExpanded)
        {
            parent.IsExpanded = false;
            var removeIndex = parentIndex + 1;
            while (removeIndex < _items.Count && _items[removeIndex].IsChildEntry)
                _items.RemoveAt(removeIndex);
        }
        else
        {
            EnsureChildren(parent);
            parent.IsExpanded = true;
            for (var i = 0; i < parent.CachedChildren!.Count; i++)
                _items.Insert(parentIndex + 1 + i, parent.CachedChildren[i]);
        }
    }

    private static void EnsureChildren(AudioFileEntry parent)
    {
        if (parent.CachedChildren != null) return;

        try
        {
            parent.CachedChildren = AudioConverterTabOperations.EnumerateChildren(
                parent);
        }
        catch
        {
            parent.CachedChildren = [];
        }
    }

    private async void ExpandAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        // Children enumerate lazily (each bank parses on demand) — build them
        // off-thread, then rebuild the flat list in one pass.
        var parents = _parentFiles.Where(p => p.IsExpandable).ToList();
        await Task.Run(() =>
        {
            foreach (var parent in parents)
                EnsureChildren(parent);
        });

        _items.Clear();
        foreach (var parent in _parentFiles)
        {
            _items.Add(parent);
            if (!parent.IsExpandable || parent.CachedChildren is not { Count: > 0 } children)
                continue;

            parent.IsExpanded = true;
            foreach (var child in children)
                _items.Add(child);
        }
    }

    private void CollapseAll_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0) return;

        foreach (var parent in _parentFiles)
            parent.IsExpanded = false;

        _items.Clear();
        foreach (var parent in _parentFiles)
            _items.Add(parent);
    }

    /// <summary>
    ///     Selected VAB output rate; 0 = auto, letting the extractor derive each
    ///     sample's rate from its tone table (center note + fine tune).
    /// </summary>
    private int GetSelectedVabSampleRate()
    {
        var selected = (VabSampleRateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        return selected switch
        {
            "11025 Hz" => 11025,
            "22050 Hz" => 22050,
            "44100 Hz" => 44100,
            _ => 0
        };
    }

    /// <summary>
    ///     Reflects the selected VAB bank's dominant tone-table rate in the
    ///     "Auto" combo item so the user can see what detection resolved to.
    /// </summary>
    private async Task UpdateDetectedVabRateAsync(AudioFileEntry parent)
    {
        if (parent.AudioFormat != "VAB")
            return;

        if (parent.CachedChildren == null)
            await Task.Run(() => EnsureChildren(parent));

        var dominant = parent.CachedChildren!
            .Where(c => c.SampleRate > 0)
            .GroupBy(c => c.SampleRate)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        VabAutoRateItem.Content = dominant > 0
            ? $"Auto (detected: {dominant} Hz)"
            : "Auto (detected)";
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.ConvertAsync(
            _parentFiles,
            _outputDir,
            GetSelectedVabSampleRate(),
            DispatcherQueue,
            ConvertButton,
            CancelButton,
            ConversionProgress);
    }

    private async void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        await _conversionController.CancelAsync(ConvertButton, CancelButton);
    }

    // ── Audio Preview ────────────────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = FilesListView.SelectedItem;

        switch (selectedItem)
        {
            case AudioFileEntry { IsExpandable: true } parent:
                ShowParentInfo(parent);
                await UpdateDetectedVabRateAsync(parent);
                break;
            case AudioFileEntry parent:
                await LoadAudioPreview(parent);
                break;
            case AudioSampleEntry sample:
                await LoadAudioPreview(sample);
                break;
            default:
                ClearPreview();
                break;
        }
    }

    private void ShowParentInfo(AudioFileEntry parent)
    {
        StopPlayback();
        PreviewPlaceholderText.Visibility = Visibility.Collapsed;
        PreviewFileNameText.Text = parent.FileName;
        PreviewInfoText.Text = parent.AudioFormat == "XA"
            ? "Multi-channel XA stream\nSelect a channel to preview"
            : $"{parent.AudioFormat} soundbank\nSelect a sample to preview";
        PreviewLoading.IsActive = false;
        AudioIcon.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";
        PreviewErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAudioPreview(IListEntry item)
    {
        var previousPreviewCts = _previewCts;
        if (previousPreviewCts != null)
        {
            _previewCts = null;
            await previousPreviewCts.CancelAsync();
            previousPreviewCts.Dispose();
        }

        var cts = new CancellationTokenSource();
        _previewCts = cts;
        // Snapshot the token before any await: tab teardown disposes _previewCts
        // (this same object) mid-flight, and the CancellationTokenSource.Token
        // GETTER throws ObjectDisposedException — the token struct stays safe.
        var token = cts.Token;

        StopPlayback();

        // Loading state
        PreviewPlaceholderText.Visibility = Visibility.Collapsed;
        PreviewLoading.IsActive = true;
        AudioIcon.Visibility = Visibility.Collapsed;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";

        // The selected VAB rate is part of the cache identity — without it,
        // changing the rate would replay a stale WAV converted at the old one.
        var vabSampleRate = GetSelectedVabSampleRate();

        AudioPreviewCacheKey cacheKey;
        string displayName;
        string infoText;

        if (item is AudioFileEntry parent)
        {
            cacheKey = new AudioPreviewCacheKey(parent, null, vabSampleRate);
            displayName = parent.FileName;
            infoText = parent.AudioFormat;
        }
        else if (item is AudioSampleEntry sample)
        {
            // Cue indices repeat across sheets and can target the same bank
            // sample. The child object is the exact session-local selection.
            cacheKey = new AudioPreviewCacheKey(sample, null, vabSampleRate);
            displayName = $"{sample.ParentFileName} — {sample.IndexDisplay}";
            infoText = sample.InfoDisplay;
        }
        else
        {
            ClearPreview();
            return;
        }

        PreviewFileNameText.Text = displayName;
        PreviewInfoText.Text = infoText;

        // Check cache
        string? wavPath = null;
        if (_previewCache.TryGetValue(cacheKey, out var cached) && File.Exists(cached))
        {
            wavPath = cached;
        }
        else
        {
            try
            {
                wavPath = await Task.Run(
                    () => AudioConverterTabOperations.ConvertForPreview(
                        item,
                        _tempDir,
                        vabSampleRate),
                    token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (token.IsCancellationRequested) return;

                PreviewLoading.IsActive = false;
                AudioIcon.Visibility = Visibility.Visible;
                PreviewErrorText.Text = $"Preview error: {ex.Message}";
                PreviewErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (wavPath != null)
                _previewCache[cacheKey] = wavPath;
        }

        if (token.IsCancellationRequested) return;

        PreviewLoading.IsActive = false;
        AudioIcon.Visibility = Visibility.Visible;

        if (wavPath != null && File.Exists(wavPath))
        {
            StartPlayback(wavPath);
        }
        else
        {
            PreviewErrorText.Text = "Failed to decode audio";
            PreviewErrorText.Visibility = Visibility.Visible;
        }
    }

    private void StartPlayback(string wavPath)
    {
        StopPlayback();

        _mediaPlayer = new MediaPlayer
        {
            Source = MediaSource.CreateFromUri(new Uri(wavPath)),
            Volume = VolumeSlider.Value / 100.0
        };
        _mediaPlayer.MediaEnded += MediaPlayer_MediaEnded;
        _mediaPlayer.MediaFailed += MediaPlayer_MediaFailed;

        _positionTimer?.Stop();
        _positionTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _positionTimer.Tick += PositionTimer_Tick;

        _mediaPlayer.Play();
        _positionTimer.Start();
        PlayPauseButton.IsEnabled = true;
        StopButton.IsEnabled = true;
        UpdatePlayPauseIcon(true);
    }

    private void StopPlayback()
    {
        if (_positionTimer != null)
        {
            _positionTimer.Stop();
            _positionTimer.Tick -= PositionTimer_Tick;
            _positionTimer = null;
        }

        if (_mediaPlayer != null)
        {
            _mediaPlayer.MediaEnded -= MediaPlayer_MediaEnded;
            _mediaPlayer.MediaFailed -= MediaPlayer_MediaFailed;
            _mediaPlayer.Pause();
            _mediaPlayer.Source = null;
            _mediaPlayer.Dispose();
            _mediaPlayer = null;
        }

        UpdatePlayPauseIcon(false);
        _updatingSlider = true;
        PlaybackSlider.Value = 0;
        _updatingSlider = false;
        _sliderTooltipConverter.DurationSeconds = 0;
        CurrentTimeText.Text = "00:00";
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;

        var session = _mediaPlayer.PlaybackSession;
        if (session.PlaybackState == MediaPlaybackState.Playing)
        {
            _mediaPlayer.Pause();
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
        }
        else
        {
            _mediaPlayer.Play();
            _positionTimer?.Start();
            UpdatePlayPauseIcon(true);
        }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mediaPlayer == null) return;
        _mediaPlayer.Pause();
        _mediaPlayer.PlaybackSession.Position = TimeSpan.Zero;
        _positionTimer?.Stop();
        UpdatePlayPauseIcon(false);
        _updatingSlider = true;
        PlaybackSlider.Value = 0;
        _updatingSlider = false;
        CurrentTimeText.Text = "00:00";
    }

    private void MediaPlayer_MediaEnded(MediaPlayer sender, object args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            _updatingSlider = true;
            PlaybackSlider.Value = 100;
            _updatingSlider = false;
        });
    }

    private void MediaPlayer_MediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _positionTimer?.Stop();
            UpdatePlayPauseIcon(false);
            PreviewErrorText.Text = $"Playback error: {args.ErrorMessage}";
            PreviewErrorText.Visibility = Visibility.Visible;
        });
    }

    private void PositionTimer_Tick(object? sender, object e)
    {
        if (_mediaPlayer == null) return;

        var session = _mediaPlayer.PlaybackSession;
        var duration = session.NaturalDuration;
        if (duration.TotalSeconds > 0)
        {
            _updatingSlider = true;
            PlaybackSlider.Value = session.Position.TotalSeconds / duration.TotalSeconds * 100;
            _updatingSlider = false;
            _sliderTooltipConverter.DurationSeconds = duration.TotalSeconds;
            CurrentTimeText.Text = FormatTime(session.Position);
            TotalTimeText.Text = FormatTime(duration);
        }
    }

    private void PlaybackSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _mediaPlayer == null) return;

        var duration = _mediaPlayer.PlaybackSession.NaturalDuration;
        if (duration.TotalSeconds > 0)
        {
            var newPosition = TimeSpan.FromSeconds(e.NewValue / 100.0 * duration.TotalSeconds);
            _mediaPlayer.PlaybackSession.Position = newPosition;
        }
    }

    private void UpdatePlayPauseIcon(bool isPlaying)
    {
        PlayPauseIcon.Glyph = isPlaying ? "\uE769" : "\uE768";
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_mediaPlayer != null)
            _mediaPlayer.Volume = e.NewValue / 100.0;

        // Applying the persisted value at startup must not write it back.
        if (!_suppressVolumeEvents)
            _persistVolume?.Invoke();
    }

    private void ClearPreview()
    {
        StopPlayback();
        _previewCts?.Cancel();
        _previewCts?.Dispose();
        _previewCts = null;
        PreviewPlaceholderText.Visibility = Visibility.Visible;
        PreviewLoading.IsActive = false;
        PreviewFileNameText.Text = "";
        PreviewInfoText.Text = "";
        PreviewErrorText.Visibility = Visibility.Collapsed;
        CurrentTimeText.Text = "00:00";
        TotalTimeText.Text = "00:00";
        PlaybackSlider.Value = 0;
    }

    private static string FormatTime(TimeSpan ts)
    {
        return AudioConverterTabOperations.FormatTime(ts);
    }

    private void AudioConverterTab_Unloaded(object sender, RoutedEventArgs e)
    {
        Dispose();
    }
}
