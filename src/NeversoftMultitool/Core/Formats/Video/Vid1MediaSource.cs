#if WINDOWS_GUI
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Core.Formats.Video;

/// <summary>
///     Direct WinUI playback source for GameCube VID1 streams.
///     Video is decoded natively in presentation order; audio is decoded to
///     PCM16 through the existing VID1 Vorbis/ffmpeg path.
/// </summary>
public sealed class Vid1MediaSource : IDisposable
{
    private const int DecodeAheadFrames = 3;

    private readonly Vid1AudioExtractor.Vid1PcmAudio? _audio;
    private readonly Vid1VideoFile _file;
    private readonly double _frameRate;
    private readonly Queue<QueuedVideoFrame> _videoQueue = [];
    private int _audioByteOffset;
    private int _nextQueuedPresentationIndex;
    private Vid1BgraPresentationFrameProvider _provider;

    private Vid1MediaSource(Vid1VideoFile file, Vid1AudioExtractor.Vid1PcmAudio? audio)
    {
        _file = file;
        _audio = audio;
        _frameRate = file.FrameRate;
        _provider = new Vid1BgraPresentationFrameProvider(file);
    }

    public void Dispose()
    {
        _videoQueue.Clear();
    }

    public static MediaSource? Create(string filePath)
    {
        if (!Vid1VideoFile.TryParse(filePath, out var file, out _))
            return null;

        Vid1AudioExtractor.Vid1PcmAudio? audio = null;
        if (Vid1AudioExtractor.TryDecodeToPcm16(filePath, out var decodedAudio, out _))
            audio = decodedAudio;

        var source = new Vid1MediaSource(file!, audio);
        var videoProps = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8,
            (uint)file!.Width,
            (uint)file.Height);
        videoProps.FrameRate.Numerator = (uint)Math.Round(file.FrameRate * 100);
        videoProps.FrameRate.Denominator = 100;

        var videoDescriptor = new VideoStreamDescriptor(videoProps);
        MediaStreamSource streamSource;

        if (audio is { Pcm16.Length: > 0 })
        {
            var audioProps = AudioEncodingProperties.CreatePcm(
                (uint)audio.SampleRate,
                (uint)audio.Channels,
                16);
            var audioDescriptor = new AudioStreamDescriptor(audioProps);
            streamSource = new MediaStreamSource(videoDescriptor, audioDescriptor);
        }
        else
        {
            streamSource = new MediaStreamSource(videoDescriptor);
        }

        streamSource.CanSeek = true;
        streamSource.Duration = file.Duration;
        streamSource.SampleRequested += source.OnSampleRequested;
        streamSource.Starting += source.OnStarting;
        streamSource.Closed += source.OnClosed;

        return MediaSource.CreateFromMediaStreamSource(streamSource);
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
    {
        var presentationIndex = 0;
        if (args.Request.StartPosition.HasValue)
        {
            var position = args.Request.StartPosition.Value;
            presentationIndex = Math.Clamp(
                (int)(position.TotalSeconds * _frameRate),
                0,
                Math.Max(0, _file.FrameCount - 1));
            _audioByteOffset = ComputeAudioOffset(position);
        }
        else
        {
            _audioByteOffset = 0;
        }

        ResetVideo(presentationIndex);
        args.Request.SetActualStartPosition(TimeSpan.FromSeconds(presentationIndex / _frameRate));
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
        EnsureVideoQueue(DecodeAheadFrames);
        if (_videoQueue.Count == 0)
        {
            request.Sample = null;
            return;
        }

        var queued = _videoQueue.Dequeue();
        var timestamp = TimeSpan.FromSeconds(queued.PresentationIndex / _frameRate);
        var duration = TimeSpan.FromSeconds(1.0 / _frameRate);
        var sample = MediaStreamSample.CreateFromBuffer(queued.Frame.Bgra8.AsBuffer(), timestamp);
        sample.Duration = duration;
        request.Sample = sample;
    }

    private void ProvideAudioSample(MediaStreamSourceSampleRequest request)
    {
        if (_audio == null || _audioByteOffset >= _audio.Pcm16.Length)
        {
            request.Sample = null;
            return;
        }

        var bytesPerVideoFrame = Math.Max(1, (int)(_audio.BytesPerSecond / _frameRate));
        bytesPerVideoFrame -= bytesPerVideoFrame % (_audio.Channels * 2);
        if (bytesPerVideoFrame <= 0)
            bytesPerVideoFrame = _audio.Channels * 2;

        var chunkSize = Math.Min(bytesPerVideoFrame, _audio.Pcm16.Length - _audioByteOffset);
        chunkSize -= chunkSize % (_audio.Channels * 2);
        if (chunkSize <= 0)
        {
            request.Sample = null;
            return;
        }

        var chunk = new byte[chunkSize];
        Buffer.BlockCopy(_audio.Pcm16, _audioByteOffset, chunk, 0, chunkSize);

        var timestamp = TimeSpan.FromSeconds((double)_audioByteOffset / _audio.BytesPerSecond);
        var duration = TimeSpan.FromSeconds((double)chunkSize / _audio.BytesPerSecond);
        var sample = MediaStreamSample.CreateFromBuffer(chunk.AsBuffer(), timestamp);
        sample.Duration = duration;
        request.Sample = sample;

        _audioByteOffset += chunkSize;
    }

    private void OnClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args)
    {
        ResetVideo(0);
        _audioByteOffset = 0;
    }

    private void ResetVideo(int presentationIndex)
    {
        _provider = new Vid1BgraPresentationFrameProvider(_file);
        _videoQueue.Clear();
        _nextQueuedPresentationIndex = 0;

        for (var i = 0; i < presentationIndex; i++)
        {
            if (_provider.DecodeNextFrame() == null)
                break;

            _nextQueuedPresentationIndex++;
        }
    }

    private void EnsureVideoQueue(int targetCount)
    {
        while (_videoQueue.Count < targetCount)
        {
            var frame = _provider.DecodeNextFrame();
            if (frame == null)
                break;

            _videoQueue.Enqueue(new QueuedVideoFrame(_nextQueuedPresentationIndex++, frame));
        }
    }

    private int ComputeAudioOffset(TimeSpan position)
    {
        if (_audio == null)
            return 0;

        var offset = Math.Clamp(
            (int)(position.TotalSeconds * _audio.BytesPerSecond),
            0,
            _audio.Pcm16.Length);
        var alignment = _audio.Channels * 2;
        return offset - offset % alignment;
    }

    private readonly record struct QueuedVideoFrame(
        int PresentationIndex,
        Vid1DecodedBgraFrame Frame);
}
#endif
