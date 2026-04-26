#if WINDOWS_GUI
using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
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
    // Extra slots cover samples still in flight while WinUI requests the next frame.
    private const int VideoBufferCount = DecodeAheadFrames + 4;

    private readonly Vid1AudioExtractor.Vid1PcmAudio? _audio;
    private readonly Vid1VideoFile _file;
    private readonly double _frameRate;
    private readonly byte[] _seekScratchBuffer;
    private readonly Queue<VideoBufferSlot> _freeVideoBuffers = new(VideoBufferCount);
    private readonly int _videoFrameByteLength;
    private readonly Queue<QueuedVideoFrame> _videoQueue = new(VideoBufferCount);
    private readonly VideoBufferSlot[] _videoBuffers = new VideoBufferSlot[VideoBufferCount];
    private readonly object _videoSync = new();
    private int _audioByteOffset;
    private bool _disposed;
    private int _nextQueuedPresentationIndex;
    private readonly Vid1BgraPresentationFrameProvider _provider;

    private Vid1MediaSource(Vid1VideoFile file, Vid1AudioExtractor.Vid1PcmAudio? audio)
    {
        _file = file;
        _audio = audio;
        _frameRate = file.FrameRate;
        _videoFrameByteLength = file.Width * file.Height * 4;
        _seekScratchBuffer = new byte[_videoFrameByteLength];
        _provider = new Vid1BgraPresentationFrameProvider(file);

        for (var i = 0; i < _videoBuffers.Length; i++)
        {
            var slot = new VideoBufferSlot(this, _videoFrameByteLength);
            _videoBuffers[i] = slot;
            _freeVideoBuffers.Enqueue(slot);
        }
    }

    public void Dispose()
    {
        lock (_videoSync)
        {
            if (_disposed)
                return;

            _disposed = true;
            RecycleQueuedVideoBuffers();

            foreach (var slot in _videoBuffers)
            {
                if (slot.State == VideoBufferSlotState.InFlight)
                {
                    slot.ReleaseWhenAvailable = true;
                    continue;
                }

                slot.ReleaseToPool();
            }

            _freeVideoBuffers.Clear();
        }
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
        lock (_videoSync)
        {
            EnsureVideoQueue(DecodeAheadFrames);
            if (_videoQueue.Count == 0)
            {
                request.Sample = null;
                return;
            }

            var queued = _videoQueue.Dequeue();
            var slot = queued.Slot;
            var timestamp = TimeSpan.FromSeconds(queued.PresentationIndex / _frameRate);
            var duration = TimeSpan.FromSeconds(1.0 / _frameRate);
            var sample = MediaStreamSample.CreateFromBuffer(slot.BufferView, timestamp);
            slot.AttachToSample(sample);
            sample.Duration = duration;
            request.Sample = sample;
        }
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

        var timestamp = TimeSpan.FromSeconds((double)_audioByteOffset / _audio.BytesPerSecond);
        var duration = TimeSpan.FromSeconds((double)chunkSize / _audio.BytesPerSecond);
        var sample = MediaStreamSample.CreateFromBuffer(_audio.Pcm16.AsBuffer(_audioByteOffset, chunkSize), timestamp);
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
        lock (_videoSync)
        {
            _provider.Reset();
            RecycleQueuedVideoBuffers();
            _nextQueuedPresentationIndex = 0;

            for (var i = 0; i < presentationIndex; i++)
            {
                if (!_provider.TryDecodeNextFrame(_seekScratchBuffer, out _))
                    break;

                _nextQueuedPresentationIndex++;
            }
        }
    }

    private void EnsureVideoQueue(int targetCount)
    {
        while (_videoQueue.Count < targetCount && _freeVideoBuffers.Count > 0)
        {
            var slot = _freeVideoBuffers.Dequeue();
            slot.MarkQueued();
            if (!_provider.TryDecodeNextFrame(slot.WritableFrame, out _))
            {
                slot.MarkFree();
                _freeVideoBuffers.Enqueue(slot);
                break;
            }

            _videoQueue.Enqueue(new QueuedVideoFrame(_nextQueuedPresentationIndex++, slot));
        }
    }

    private void RecycleQueuedVideoBuffers()
    {
        while (_videoQueue.Count > 0)
        {
            var slot = _videoQueue.Dequeue().Slot;
            if (_disposed)
            {
                slot.ReleaseToPool();
                continue;
            }

            slot.MarkFree();
            _freeVideoBuffers.Enqueue(slot);
        }
    }

    private void OnVideoSlotProcessed(VideoBufferSlot slot)
    {
        lock (_videoSync)
        {
            if (_disposed || slot.ReleaseWhenAvailable)
            {
                slot.ReleaseToPool();
                return;
            }

            slot.MarkFree();
            _freeVideoBuffers.Enqueue(slot);
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

    private enum VideoBufferSlotState
    {
        Free,
        Queued,
        InFlight,
        Released
    }

    private sealed class VideoBufferSlot
    {
        private readonly TypedEventHandler<MediaStreamSample, object> _processedHandler;

        public VideoBufferSlot(Vid1MediaSource owner, int frameByteLength)
        {
            Owner = owner;
            FrameByteLength = frameByteLength;
            Bgra8 = ArrayPool<byte>.Shared.Rent(frameByteLength);
            BufferView = Bgra8.AsBuffer(0, frameByteLength);
            _processedHandler = OnProcessed;
        }

        private int FrameByteLength { get; }
        private Vid1MediaSource Owner { get; }
        public byte[] Bgra8 { get; }
        public IBuffer BufferView { get; }
        public bool ReleaseWhenAvailable { get; set; }
        public VideoBufferSlotState State { get; private set; }
        public Span<byte> WritableFrame => Bgra8.AsSpan(0, FrameByteLength);

        public void MarkQueued()
        {
            ReleaseWhenAvailable = false;
            State = VideoBufferSlotState.Queued;
        }

        public void AttachToSample(MediaStreamSample sample)
        {
            State = VideoBufferSlotState.InFlight;
            sample.Processed += _processedHandler;
        }

        public void MarkFree()
        {
            ReleaseWhenAvailable = false;
            State = VideoBufferSlotState.Free;
        }

        public void ReleaseToPool()
        {
            if (State == VideoBufferSlotState.Released)
                return;

            ArrayPool<byte>.Shared.Return(Bgra8);
            ReleaseWhenAvailable = false;
            State = VideoBufferSlotState.Released;
        }

        private void OnProcessed(MediaStreamSample sender, object args)
        {
            sender.Processed -= _processedHandler;
            Owner.OnVideoSlotProcessed(this);
        }
    }

    private readonly record struct QueuedVideoFrame(
        int PresentationIndex,
        VideoBufferSlot Slot);
}
#endif
