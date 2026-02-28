using System.Collections.ObjectModel;
using System.Diagnostics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool;

public sealed partial class AudioConverterTab : UserControl
{
    private static readonly string[] SupportedExtensions = [".adx", ".xa", ".vab", ".vag", ".kat", ".pss"];

    private readonly ObservableCollection<IListEntry> _items = [];
    private readonly List<AudioFileEntry> _parentFiles = [];
    private readonly Dictionary<string, string> _previewCache = [];

    // Temp file cache
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "NeversoftMultitool", "AudioPreview");
    private CancellationTokenSource? _cts;
    private string _inputDir = "";

    // Playback
    private MediaPlayer? _mediaPlayer;
    private string _outputDir = "";
    private DispatcherTimer? _positionTimer;
    private CancellationTokenSource? _previewCts;
    private bool _updatingSlider;

    public AudioConverterTab()
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

        ClearPreview();
        _previewCache.Clear();

        _items.Clear();
        _parentFiles.Clear();
        var allFiles = Directory.GetFiles(_inputDir);
        var audioFiles = allFiles
            .Where(f => SupportedExtensions.Contains(
                Path.GetExtension(f).ToLowerInvariant()))
            .ToList();

        // Probe extensionless files for SPU-ADPCM audio
        audioFiles.AddRange(allFiles
            .Where(f => string.IsNullOrEmpty(Path.GetExtension(f)))
            .Where(f => VagDecoder.Probe(f) != null));

        foreach (var filePath in audioFiles.OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase))
        {
            var fileName = Path.GetFileName(filePath)!;
            var ext = Path.GetExtension(filePath).ToLowerInvariant();
            var entry = new AudioFileEntry
            {
                FileName = fileName,
                AudioFormat = DetectFormat(ext)
            };
            _parentFiles.Add(entry);
            _items.Add(entry);
        }

        UpdateUiState();
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
            if (parent.CachedChildren == null)
            {
                var inputFile = Path.Combine(_inputDir, parent.FileName);
                try
                {
                    parent.CachedChildren = parent.AudioFormat switch
                    {
                        "VAB" => VabExtractor.EnumerateSamples(inputFile)
                            .Select(s => new AudioSampleEntry
                            {
                                ParentFileName = parent.FileName,
                                SampleIndex = s.Index,
                                Encoding = "SPU-ADPCM",
                                SampleRate = 0, // user-selected, not in header
                                Channels = 1,
                                DataSize = s.DataSize
                            }).ToList(),
                        "KAT" => KatExtractor.EnumerateSamples(inputFile)
                            .Select(s => new AudioSampleEntry
                            {
                                ParentFileName = parent.FileName,
                                SampleIndex = s.Index,
                                Encoding = s.Encoding,
                                SampleRate = s.SampleRate,
                                Channels = s.Channels,
                                DataSize = s.DataSize
                            }).ToList(),
                        _ => []
                    };
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

    private int GetSelectedVabSampleRate()
    {
        var selected = (VabSampleRateCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
        return selected switch
        {
            "22050 Hz" => 22050,
            "44100 Hz" => 44100,
            _ => 11025
        };
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_parentFiles.Count == 0 || string.IsNullOrEmpty(_outputDir)) return;

        _cts = new CancellationTokenSource();

        foreach (var file in _parentFiles)
        {
            file.SampleCount = 0;
            file.Status = ExtractionStatus.Pending;
        }

        ConvertButton.IsEnabled = false;
        CancelButton.Visibility = Visibility.Visible;
        ConversionProgress.Visibility = Visibility.Visible;
        ConversionProgress.Value = 0;

        var stopwatch = Stopwatch.StartNew();
        var filesProcessed = 0;
        var totalFiles = _parentFiles.Count;
        var token = _cts.Token;
        var dispatcher = DispatcherQueue;
        var inputDir = _inputDir;
        var outputDir = _outputDir;
        var vabSampleRate = GetSelectedVabSampleRate();

        // Snapshot parent entries for iteration
        var entries = _parentFiles.ToList();

        try
        {
            await Task.Run(() =>
            {
                foreach (var entry in entries)
                {
                    if (token.IsCancellationRequested) break;

                    dispatcher.TryEnqueue(() => entry.Status = ExtractionStatus.Processing);

                    var inputFile = Path.Combine(inputDir, entry.FileName);

                    try
                    {
                        var result = entry.AudioFormat switch
                        {
                            "ADX" => AdxDecoder.ConvertToWav(inputFile, outputDir),
                            "XA" => XaDecoder.ConvertToWav(inputFile, outputDir),
                            "VAB" => VabExtractor.ExtractToWav(inputFile, outputDir, vabSampleRate),
                            "VAG" => VagDecoder.ConvertToWav(inputFile, outputDir),
                            "KAT" => KatExtractor.ExtractToWav(inputFile, outputDir),
                            _ => new AudioConvertResult { ErrorMessage = "Unknown format" }
                        };

                        var processed = Interlocked.Increment(ref filesProcessed);

                        dispatcher.TryEnqueue(() =>
                        {
                            entry.SampleCount = result.SamplesWritten;
                            entry.Status = result.Success
                                ? ExtractionStatus.Done
                                : ExtractionStatus.Error;
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
            }, token);

            stopwatch.Stop();
            ConversionProgress.Value = 100;
            MainWindow.Instance?.SetStatus(
                $"Converted {filesProcessed} files in {stopwatch.Elapsed.TotalSeconds:F2}s");
        }
        catch (OperationCanceledException)
        {
            MainWindow.Instance?.SetStatus("Conversion cancelled");
        }
        finally
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConvertButton.IsEnabled = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        CancelButton.Visibility = Visibility.Collapsed;
        ConvertButton.IsEnabled = true;
        MainWindow.Instance?.SetStatus("Conversion cancelled");
    }

    private static string DetectFormat(string extension)
    {
        return extension switch
        {
            ".adx" => "ADX",
            ".xa" => "XA",
            ".vab" => "VAB",
            ".vag" or ".pss" => "VAG",
            ".kat" => "KAT",
            "" => "VAG",
            _ => "Unknown"
        };
    }

    // ── Audio Preview ────────────────────────────────────────────────────

    private async void FilesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selectedItem = FilesListView.SelectedItem;

        switch (selectedItem)
        {
            case AudioFileEntry { IsExpandable: true } parent:
                ShowParentInfo(parent);
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
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(280);
        PreviewFileNameText.Text = parent.FileName;
        PreviewInfoText.Text = $"{parent.AudioFormat} soundbank\nSelect a sample to preview";
        PreviewLoading.IsActive = false;
        AudioIcon.Visibility = Visibility.Visible;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        PreviewErrorText.Visibility = Visibility.Collapsed;
    }

    private async Task LoadAudioPreview(IListEntry item)
    {
        _previewCts?.Cancel();
        var cts = new CancellationTokenSource();
        _previewCts = cts;

        StopPlayback();

        // Show preview panel with loading state
        PreviewPanel.Visibility = Visibility.Visible;
        PreviewColumn.Width = new GridLength(280);
        PreviewLoading.IsActive = true;
        AudioIcon.Visibility = Visibility.Collapsed;
        PreviewErrorText.Visibility = Visibility.Collapsed;
        PlayPauseButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        PlaybackSlider.Value = 0;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";

        string cacheKey;
        string displayName;
        string infoText;

        if (item is AudioFileEntry parent)
        {
            cacheKey = Path.Combine(_inputDir, parent.FileName);
            displayName = parent.FileName;
            infoText = parent.AudioFormat;
        }
        else if (item is AudioSampleEntry sample)
        {
            cacheKey = $"{Path.Combine(_inputDir, sample.ParentFileName)}#{sample.SampleIndex}";
            displayName = $"{sample.ParentFileName} #{sample.SampleIndex:D3}";
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
                var vabSampleRate = GetSelectedVabSampleRate();
                wavPath = await Task.Run(() => ConvertForPreview(item, vabSampleRate), cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (cts.Token.IsCancellationRequested) return;

                PreviewLoading.IsActive = false;
                AudioIcon.Visibility = Visibility.Visible;
                PreviewErrorText.Text = $"Preview error: {ex.Message}";
                PreviewErrorText.Visibility = Visibility.Visible;
                return;
            }

            if (wavPath != null)
                _previewCache[cacheKey] = wavPath;
        }

        if (cts.Token.IsCancellationRequested) return;

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

    private string? ConvertForPreview(IListEntry item, int vabSampleRate)
    {
        Directory.CreateDirectory(_tempDir);

        if (item is AudioFileEntry parent)
        {
            var inputFile = Path.Combine(_inputDir, parent.FileName);
            var result = parent.AudioFormat switch
            {
                "ADX" => AdxDecoder.ConvertToWav(inputFile, _tempDir),
                "XA" => XaDecoder.ConvertToWav(inputFile, _tempDir),
                "VAG" => VagDecoder.ConvertToWav(inputFile, _tempDir),
                _ => null
            };

            if (result is not { Success: true }) return null;

            var stem = Path.GetFileNameWithoutExtension(parent.FileName);

            // ADX produces {stem}.wav directly; XA may produce {stem}.wav or {stem}/ch00.wav
            var wavPath = Path.Combine(_tempDir, stem + ".wav");
            if (File.Exists(wavPath)) return wavPath;

            // XA multi-channel: pick first channel
            var channelPath = Path.Combine(_tempDir, stem, "ch00.wav");
            return File.Exists(channelPath) ? channelPath : null;
        }

        if (item is AudioSampleEntry sample)
        {
            var inputFile = Path.Combine(_inputDir, sample.ParentFileName);
            var parentEntry = _parentFiles.FirstOrDefault(p => p.FileName == sample.ParentFileName);
            if (parentEntry == null) return null;

            return parentEntry.AudioFormat switch
            {
                "VAB" => VabExtractor.ExtractSingleToWav(
                    inputFile, sample.SampleIndex, _tempDir, vabSampleRate),
                "KAT" => KatExtractor.ExtractSingleToWav(
                    inputFile, sample.SampleIndex, _tempDir),
                _ => null
            };
        }

        return null;
    }

    private void StartPlayback(string wavPath)
    {
        _mediaPlayer?.Dispose();
        _mediaPlayer = new MediaPlayer();
        _mediaPlayer.Source = MediaSource.CreateFromUri(new Uri(wavPath));
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
        _positionTimer?.Stop();
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
        CurrentTimeText.Text = "0:00";
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
        CurrentTimeText.Text = "0:00";
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

    private void ClearPreview()
    {
        StopPlayback();
        _previewCts?.Cancel();
        PreviewPanel.Visibility = Visibility.Collapsed;
        PreviewColumn.Width = new GridLength(0);
        PreviewLoading.IsActive = false;
        PreviewFileNameText.Text = "";
        PreviewInfoText.Text = "";
        PreviewErrorText.Visibility = Visibility.Collapsed;
        CurrentTimeText.Text = "0:00";
        TotalTimeText.Text = "0:00";
        PlaybackSlider.Value = 0;
    }

    private static string FormatTime(TimeSpan ts)
    {
        return ts.TotalMinutes >= 60
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
    }
}
