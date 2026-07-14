#if WINDOWS_GUI
using System.Buffers;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using NeversoftMultitool.Core.Formats.Audio;

namespace NeversoftMultitool.Core.Formats.Vid1;

/// <summary>
///     Direct WinUI playback source for GameCube VID1 streams.
///     A dedicated worker thread decodes ahead into a bounded frame queue;
///     sample requests that outrun the decoder take a deferral instead of
///     blocking the media pipeline (the old synchronous design froze playback
///     whenever decode fell behind real time, and ended the stream outright on
///     any mid-stream decode hiccup). Seeks reposition on the worker: forward
///     seeks fast-forward the decoder, backward seeks restart it (VID1 frames
///     are inter-predicted with no random access), so scrubbing never stalls
///     the UI thread.
/// </summary>
public sealed class Vid1MediaSource : IDisposable
{
    private const int QueueDepth = 24;
    // Extra slots cover samples still in flight while WinUI requests the next frame.
    private const int VideoBufferCount = QueueDepth + 8;

    private readonly Vid1AudioExtractor.Vid1PcmAudio? _audio;
    private readonly Vid1VideoFile _file;
    private readonly double _frameRate;
    private readonly Queue<VideoBufferSlot> _freeVideoBuffers = new(VideoBufferCount);
    private readonly byte[] _lastGoodFrame;
    private readonly Vid1BgraPresentationFrameProvider _provider;
    private readonly byte[] _seekScratchBuffer;
    private readonly object _sync = new();
    private readonly VideoBufferSlot[] _videoBuffers = new VideoBufferSlot[VideoBufferCount];
    private readonly Queue<QueuedVideoFrame> _videoQueue = new(VideoBufferCount);
    private int _audioByteOffset;
    private bool _disposed;
    private bool _endOfStream;
    private bool _hasLastGoodFrame;
    private int _nextDecodeIndex;
    private MediaStreamSourceSampleRequestDeferral? _pendingVideoDeferral;
    private MediaStreamSourceSampleRequest? _pendingVideoRequest;
    private int _pendingSeekIndex = -1;
    private Thread? _worker;

    private Vid1MediaSource(Vid1VideoFile file, Vid1AudioExtractor.Vid1PcmAudio? audio)
    {
        _file = file;
        _audio = audio;
        _frameRate = file.FrameRate;
        var videoFrameByteLength = file.Width * file.Height * 4;
        _seekScratchBuffer = new byte[videoFrameByteLength];
        _lastGoodFrame = new byte[videoFrameByteLength];
        _provider = new Vid1BgraPresentationFrameProvider(file);

        for (var i = 0; i < _videoBuffers.Length; i++)
        {
            var slot = new VideoBufferSlot(this, videoFrameByteLength);
            _videoBuffers[i] = slot;
            _freeVideoBuffers.Enqueue(slot);
        }
    }

    public void Dispose()
    {
        Thread? worker;
        lock (_sync)
        {
            if (_disposed)
                return;

            _disposed = true;
            worker = _worker;
            _worker = null;
            CompletePendingVideoRequestLocked(deliver: false);
            Monitor.PulseAll(_sync);
        }

        worker?.Join(TimeSpan.FromSeconds(3));

        lock (_sync)
        {
            RecycleQueuedVideoBuffersLocked();

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
        streamSource.BufferTime = TimeSpan.Zero;
        streamSource.SampleRequested += source.OnSampleRequested;
        streamSource.Starting += source.OnStarting;
        streamSource.Closed += source.OnClosed;

        source.StartWorker();
        return MediaSource.CreateFromMediaStreamSource(streamSource);
    }

    private void StartWorker()
    {
        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Name = "Vid1 decode"
        };
        _worker = worker;
        worker.Start();
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
            _audioByteOffset = ComputeAudioOffset(TimeSpan.FromSeconds(presentationIndex / _frameRate));
        }
        else
        {
            _audioByteOffset = 0;
        }

        lock (_sync)
        {
            _pendingSeekIndex = presentationIndex;
            Monitor.PulseAll(_sync);
        }

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
        lock (_sync)
        {
            if (_videoQueue.Count > 0)
            {
                DeliverVideoSampleLocked(request);
                return;
            }

            if (_endOfStream || _disposed)
            {
                request.Sample = null;
                return;
            }

            // Decoder hasn't caught up — defer; the worker completes this as
            // soon as the next frame lands in the queue.
            _pendingVideoRequest = request;
            _pendingVideoDeferral = request.GetDeferral();
            Monitor.PulseAll(_sync);
        }
    }

    private void DeliverVideoSampleLocked(MediaStreamSourceSampleRequest request)
    {
        var queued = _videoQueue.Dequeue();
        var slot = queued.Slot;
        var timestamp = TimeSpan.FromSeconds(queued.PresentationIndex / _frameRate);
        var sample = MediaStreamSample.CreateFromBuffer(slot.BufferView, timestamp);
        slot.AttachToSample(sample);
        sample.Duration = TimeSpan.FromSeconds(1.0 / _frameRate);
        request.Sample = sample;
        Monitor.PulseAll(_sync);
    }

    /// <summary>Worker → pipeline handoff when a deferred request is waiting.</summary>
    private void CompletePendingVideoRequestLocked(bool deliver)
    {
        if (_pendingVideoDeferral == null)
            return;

        if (deliver && _videoQueue.Count > 0)
            DeliverVideoSampleLocked(_pendingVideoRequest!);
        else if (!deliver || _endOfStream)
            _pendingVideoRequest!.Sample = null;
        else
            return;

        _pendingVideoDeferral.Complete();
        _pendingVideoRequest = null;
        _pendingVideoDeferral = null;
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
        Dispose();
    }

    // ─── Decode worker ────────────────────────────────────────────────────

    private void WorkerLoop()
    {
        while (true)
        {
            VideoBufferSlot? slot = null;
            int seekTarget;

            lock (_sync)
            {
                while (true)
                {
                    if (_disposed)
                        return;

                    seekTarget = _pendingSeekIndex;
                    if (seekTarget >= 0)
                    {
                        _pendingSeekIndex = -1;
                        _endOfStream = false;
                        RecycleQueuedVideoBuffersLocked();
                        break;
                    }

                    if (!_endOfStream && _videoQueue.Count < QueueDepth && _freeVideoBuffers.Count > 0)
                    {
                        slot = _freeVideoBuffers.Dequeue();
                        slot.MarkQueued();
                        break;
                    }

                    Monitor.Wait(_sync);
                }
            }

            if (seekTarget >= 0)
            {
                Reposition(seekTarget);
                continue;
            }

            DecodeIntoSlot(slot!);
        }
    }

    /// <summary>
    ///     VID1 has no random access (frames are inter-predicted): forward
    ///     seeks decode-and-discard up to the target; backward seeks restart
    ///     the provider first. A newer seek arriving mid-scan supersedes this
    ///     one, so scrubbing stays responsive.
    /// </summary>
    private void Reposition(int targetIndex)
    {
        if (targetIndex < _nextDecodeIndex)
        {
            _provider.Reset();
            _nextDecodeIndex = 0;
            _hasLastGoodFrame = false;
        }

        while (_nextDecodeIndex < targetIndex)
        {
            lock (_sync)
            {
                if (_disposed || _pendingSeekIndex >= 0)
                    return;
            }

            bool ok;
            try
            {
                ok = _provider.TryDecodeNextFrame(_seekScratchBuffer, out _);
            }
            catch
            {
                ok = false;
            }

            if (!ok)
            {
                lock (_sync)
                {
                    _endOfStream = true;
                    CompletePendingVideoRequestLocked(deliver: true);
                }

                return;
            }

            _seekScratchBuffer.CopyTo(_lastGoodFrame, 0);
            _hasLastGoodFrame = true;
            _nextDecodeIndex++;
        }
    }

    private void DecodeIntoSlot(VideoBufferSlot slot)
    {
        bool ok;
        try
        {
            ok = _provider.TryDecodeNextFrame(slot.WritableFrame, out _);
        }
        catch
        {
            ok = false;
        }

        lock (_sync)
        {
            if (_disposed)
            {
                slot.ReleaseToPool();
                return;
            }

            if (_pendingSeekIndex >= 0)
            {
                slot.MarkFree();
                _freeVideoBuffers.Enqueue(slot);
                return;
            }

            if (ok)
            {
                slot.WritableFrame.CopyTo(_lastGoodFrame);
                _hasLastGoodFrame = true;
                _videoQueue.Enqueue(new QueuedVideoFrame(_nextDecodeIndex++, slot));
            }
            else if (_nextDecodeIndex < _file.FrameCount && _hasLastGoodFrame)
            {
                // Mid-stream decode hiccup: hold the last good frame instead of
                // ending playback (the old behavior made such files "not play").
                _lastGoodFrame.CopyTo(slot.WritableFrame);
                _videoQueue.Enqueue(new QueuedVideoFrame(_nextDecodeIndex++, slot));
            }
            else
            {
                slot.MarkFree();
                _freeVideoBuffers.Enqueue(slot);
                _endOfStream = true;
            }

            CompletePendingVideoRequestLocked(deliver: true);
            Monitor.PulseAll(_sync);
        }
    }

    private void RecycleQueuedVideoBuffersLocked()
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
        lock (_sync)
        {
            if (_disposed || slot.ReleaseWhenAvailable)
            {
                slot.ReleaseToPool();
                return;
            }

            slot.MarkFree();
            _freeVideoBuffers.Enqueue(slot);
            Monitor.PulseAll(_sync);
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
