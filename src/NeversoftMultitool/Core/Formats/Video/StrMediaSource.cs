#if WINDOWS_GUI
using System.Runtime.InteropServices.WindowsRuntime;
using NeversoftMultitool.Core.Formats.Audio;
using Windows.Media.Core;
using Windows.Media.MediaProperties;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Creates a <see cref="MediaSource" /> from PS1 STR video data for direct playback
///     in a <see cref="Windows.Media.Playback.MediaPlayer" /> without ffmpeg conversion.
///     Uses <see cref="MediaStreamSource" /> to feed decoded MDEC video frames and XA audio on demand.
/// </summary>
public sealed class StrMediaSource : IDisposable
{
    private readonly List<StrDemuxer.StrFrame> _frames;
    private readonly double _frameRate;
    private readonly int _width;
    private readonly int _height;

    // Audio data (null if no audio)
    private readonly byte[]? _audioBytes; // PCM16 LE interleaved
    private readonly int _audioSampleRate;
    private readonly int _audioChannels;

    private int _frameIndex;
    private int _audioByteOffset;

    private StrMediaSource(List<StrDemuxer.StrFrame> frames, double frameRate, int width, int height,
        byte[]? audioBytes, int audioSampleRate, int audioChannels)
    {
        _frames = frames;
        _frameRate = frameRate;
        _width = width;
        _height = height;
        _audioBytes = audioBytes;
        _audioSampleRate = audioSampleRate;
        _audioChannels = audioChannels;
    }

    /// <summary>
    ///     Creates a <see cref="MediaSource" /> for direct playback of STR video data with audio.
    ///     Returns null if the data is not a valid STR file or contains no frames.
    /// </summary>
    public static MediaSource? Create(byte[] strData)
    {
        if (!StrDemuxer.IsStrFile(strData))
            return null;

        var frames = StrDemuxer.EnumerateFrames(strData).ToList();
        if (frames.Count == 0)
            return null;

        var width = frames[0].Width;
        var height = frames[0].Height;
        var frameRate = StrDemuxer.GetFrameRate(strData);
        var duration = TimeSpan.FromSeconds(frames.Count / frameRate);

        // Decode audio if present
        byte[]? audioBytes = null;
        var audioSampleRate = 37800;
        var audioChannels = 2;

        if (StrDemuxer.HasAudio(strData))
        {
            var audioSectors = StrDemuxer.ExtractAudioSectors(strData);
            var decoded = XaDecoder.DecodeToSamples(audioSectors);
            if (decoded.HasValue)
            {
                var (samples, rate, channels) = decoded.Value;
                audioSampleRate = rate;
                audioChannels = channels;
                // Convert short[] to byte[] (PCM16 LE)
                audioBytes = new byte[samples.Length * 2];
                Buffer.BlockCopy(samples, 0, audioBytes, 0, audioBytes.Length);
            }
        }

        var source = new StrMediaSource(frames, frameRate, width, height, audioBytes, audioSampleRate, audioChannels);

        // Video descriptor: BGRA8 uncompressed at 15fps
        var videoProps = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, (uint)width, (uint)height);
        // Express framerate as a rational number (multiply by 100 to preserve decimals like 12.5)
        videoProps.FrameRate.Numerator = (uint)Math.Round(frameRate * 100);
        videoProps.FrameRate.Denominator = 100;
        var videoDescriptor = new VideoStreamDescriptor(videoProps);

        // Build the stream source
        MediaStreamSource streamSource;

        if (audioBytes != null && audioBytes.Length > 0)
        {
            var audioProps = AudioEncodingProperties.CreatePcm(
                (uint)audioSampleRate, (uint)audioChannels, 16);
            var audioDescriptor = new AudioStreamDescriptor(audioProps);

            streamSource = new MediaStreamSource(videoDescriptor, audioDescriptor);
        }
        else
        {
            streamSource = new MediaStreamSource(videoDescriptor);
        }

        streamSource.CanSeek = true;
        streamSource.Duration = duration;
        streamSource.SampleRequested += source.OnSampleRequested;
        streamSource.Starting += source.OnStarting;
        streamSource.Closed += source.OnClosed;

        return MediaSource.CreateFromMediaStreamSource(streamSource);
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        if (args.Request.StartPosition.HasValue)
        {
            var position = args.Request.StartPosition.Value;
            _frameIndex = Math.Clamp((int)(position.TotalSeconds * _frameRate), 0, _frames.Count - 1);

            // Sync audio position to video
            if (_audioBytes != null)
            {
                var audioBytesPerSecond = _audioSampleRate * _audioChannels * 2; // 16-bit samples
                _audioByteOffset = Math.Clamp(
                    (int)(position.TotalSeconds * audioBytesPerSecond),
                    0, _audioBytes.Length);
                // Align to frame boundary (channels * 2 bytes per sample)
                _audioByteOffset -= _audioByteOffset % (_audioChannels * 2);
            }
        }
        else
        {
            _frameIndex = 0;
            _audioByteOffset = 0;
        }

        args.Request.SetActualStartPosition(
            TimeSpan.FromSeconds(_frameIndex / _frameRate));
    }

    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        if (args.Request.StreamDescriptor is VideoStreamDescriptor)
            ProvideVideoSample(args.Request);
        else if (args.Request.StreamDescriptor is AudioStreamDescriptor)
            ProvideAudioSample(args.Request);
    }

    private void ProvideVideoSample(MediaStreamSourceSampleRequest request)
    {
        if (_frameIndex >= _frames.Count)
        {
            request.Sample = null;
            return;
        }

        var frame = _frames[_frameIndex];
        var rgb = MdecDecoder.DecodeFrame(frame.Data, _width, _height);
        var bgra = ConvertRgb24ToBgra8(rgb, _width, _height);

        var timestamp = TimeSpan.FromSeconds(_frameIndex / _frameRate);
        var duration = TimeSpan.FromSeconds(1.0 / _frameRate);

        var sample = MediaStreamSample.CreateFromBuffer(bgra.AsBuffer(), timestamp);
        sample.Duration = duration;
        request.Sample = sample;

        _frameIndex++;
    }

    private void ProvideAudioSample(MediaStreamSourceSampleRequest request)
    {
        if (_audioBytes == null || _audioByteOffset >= _audioBytes.Length)
        {
            request.Sample = null;
            return;
        }

        // Provide ~1 video frame's worth of audio per request for smooth interleaving
        var audioBytesPerSecond = _audioSampleRate * _audioChannels * 2;
        var bytesPerVideoFrame = (int)(audioBytesPerSecond / _frameRate);
        // Align to sample frame boundary
        bytesPerVideoFrame -= bytesPerVideoFrame % (_audioChannels * 2);

        var remaining = _audioBytes.Length - _audioByteOffset;
        var chunkSize = Math.Min(bytesPerVideoFrame, remaining);
        if (chunkSize <= 0)
        {
            request.Sample = null;
            return;
        }

        var chunk = new byte[chunkSize];
        Buffer.BlockCopy(_audioBytes, _audioByteOffset, chunk, 0, chunkSize);

        var timestamp = TimeSpan.FromSeconds((double)_audioByteOffset / audioBytesPerSecond);
        var duration = TimeSpan.FromSeconds((double)chunkSize / audioBytesPerSecond);

        var sample = MediaStreamSample.CreateFromBuffer(chunk.AsBuffer(), timestamp);
        sample.Duration = duration;
        request.Sample = sample;

        _audioByteOffset += chunkSize;
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
    {
        _frameIndex = 0;
        _audioByteOffset = 0;
    }

    private static byte[] ConvertRgb24ToBgra8(byte[] rgb, int width, int height)
    {
        var pixelCount = width * height;
        var bgra = new byte[pixelCount * 4];

        for (var i = 0; i < pixelCount; i++)
        {
            var srcIdx = i * 3;
            var dstIdx = i * 4;
            bgra[dstIdx] = rgb[srcIdx + 2];     // B
            bgra[dstIdx + 1] = rgb[srcIdx + 1]; // G
            bgra[dstIdx + 2] = rgb[srcIdx];      // R
            bgra[dstIdx + 3] = 0xFF;             // A
        }

        return bgra;
    }

    public void Dispose()
    {
        _frames.Clear();
    }
}
#endif
