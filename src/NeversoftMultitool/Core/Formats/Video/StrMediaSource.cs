#if WINDOWS_GUI
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using NeversoftMultitool.Core.Formats.Audio;
#endif

namespace NeversoftMultitool.Core.Formats.Video;

internal readonly record struct StrMediaSourceSeekPosition(
    int FrameIndex,
    TimeSpan ActualPosition,
    int AudioByteOffset);

internal static class StrMediaSourceSeekAlignment
{
    internal static StrMediaSourceSeekPosition AlignExplicit(
        TimeSpan requestedPosition,
        double frameRate,
        int frameCount,
        int audioSampleRate,
        int audioChannels,
        int audioByteLength)
    {
        if (!double.IsFinite(frameRate) || frameRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameRate));
        if (frameCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        if (audioSampleRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(audioSampleRate));
        if (audioChannels <= 0)
            throw new ArgumentOutOfRangeException(nameof(audioChannels));
        if (audioByteLength < 0)
            throw new ArgumentOutOfRangeException(nameof(audioByteLength));

        var requestedFrame = Math.Floor(requestedPosition.TotalSeconds * frameRate);
        var frameIndex = requestedFrame <= 0
            ? 0
            : requestedFrame >= frameCount - 1
                ? frameCount - 1
                : (int)requestedFrame;
        var actualSeconds = frameIndex / frameRate;
        var actualPosition = TimeSpan.FromSeconds(actualSeconds);

        var bytesPerSampleFrame = checked(audioChannels * sizeof(short));
        var completeAudioBytes = audioByteLength - audioByteLength % bytesPerSampleFrame;
        // Round forward in sample-frame units; truncating bytes first can land one
        // complete PCM frame before the actual video-frame boundary.
        var firstSampleFrame = Math.Ceiling(actualPosition.TotalSeconds * audioSampleRate);
        if (TimeSpan.FromSeconds(firstSampleFrame / audioSampleRate) < actualPosition)
            firstSampleFrame++;
        var requestedAudioBytes = firstSampleFrame * bytesPerSampleFrame;
        var audioByteOffset = requestedAudioBytes >= completeAudioBytes
            ? completeAudioBytes
            : (int)requestedAudioBytes;

        return new StrMediaSourceSeekPosition(frameIndex, actualPosition, audioByteOffset);
    }
}

#if WINDOWS_GUI
/// <summary>
///     Creates a <see cref="MediaSource" /> from PS1 STR video data for direct playback
///     in a <see cref="Windows.Media.Playback.MediaPlayer" /> without ffmpeg conversion.
///     Uses <see cref="MediaStreamSource" /> to feed decoded MDEC video frames and XA audio on demand.
/// </summary>
public sealed class StrMediaSource : IDisposable
{
    // Audio data (null if no audio)
    private readonly byte[]? _audioBytes; // PCM16 LE interleaved
    private readonly int _audioChannels;
    private readonly int _audioSampleRate;
    private readonly double _frameRate;
    private readonly List<StrDemuxer.StrFrame> _frames;
    private readonly int _height;
    private readonly int _width;
    private int _audioByteOffset;

    private int _frameIndex;

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

    public void Dispose()
    {
        _frames.Clear();
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
        TimeSpan actualPosition;
        if (args.Request.StartPosition.HasValue)
        {
            var aligned = StrMediaSourceSeekAlignment.AlignExplicit(
                args.Request.StartPosition.Value,
                _frameRate,
                _frames.Count,
                _audioSampleRate,
                _audioChannels,
                _audioBytes?.Length ?? 0);
            _frameIndex = aligned.FrameIndex;
            _audioByteOffset = aligned.AudioByteOffset;
            actualPosition = aligned.ActualPosition;
        }
        else
        {
            _frameIndex = 0;
            _audioByteOffset = 0;
            actualPosition = TimeSpan.Zero;
        }

        args.Request.SetActualStartPosition(actualPosition);
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
        var bgra = StrPreviewFrameDecoder.DecodeBgra8OrBlack(frame.Data, _width, _height);

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

}
#endif
