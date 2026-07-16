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
///     the UI thread. Startup avoids stutter two ways: the pipeline's initial
///     Starting at position 0 reuses the pre-decoded queue instead of
///     repositioning (<see cref="Vid1StartupSeekPolicy" />), and every Starting
///     handshake holds a deferral until a small pre-roll of frames is queued.
/// </summary>
public sealed class Vid1MediaSource : IDisposable
{
    private const int QueueDepth = 24;
    // Extra slots cover samples still in flight while WinUI requests the next frame.
    private const int VideoBufferCount = QueueDepth + 8;
    // Frames the queue should hold before a Starting handshake completes
    // (~200 ms at 29.97 fps) — enough headroom that the reorder hold and an
    // occasional slow decode don't make the opening cadence uneven.
    private const int PrerollFrameCount = 6;
    // Safety valve: a short or broken file (or a decoder stall) must still
    // start rather than hold the pipeline's Starting handshake open forever.
    private static readonly TimeSpan PrerollTimeout = TimeSpan.FromMilliseconds(500);

    private readonly Vid1AudioExtractor.Vid1PcmAudio? _audio;
    private readonly Vid1VideoFile _file;
    private readonly double _frameRate;
    private readonly Queue<VideoBufferSlot> _freeVideoBuffers = new(VideoBufferCount);
    private readonly byte[] _lastGoodFrame;
    private readonly Vid1BgraPresentationFrameProvider _provider;
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
    private bool _startHandled;
    private MediaStreamSourceStartingRequestDeferral? _startingDeferral;
    private int _startingDeferralGeneration;
    private Timer? _startingDeferralTimer;
    private Thread? _worker;

    private Vid1MediaSource(Vid1VideoFile file, Vid1AudioExtractor.Vid1PcmAudio? audio)
    {
        _file = file;
        _audio = audio;
        _frameRate = file.FrameRate;
        var videoFrameByteLength = file.Width * file.Height * 4;
        _lastGoodFrame = new byte[videoFrameByteLength];
        _provider = new Vid1BgraPresentationFrameProvider(file, enableSeekAnchors: true);

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
            CompleteStartingDeferralLocked();
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
        // Pre-roll is enforced via the Starting deferral (see OnStarting), not
        // BufferTime: pipeline-side buffering of uncompressed BGRA frames is
        // expensive and adds latency to every seek, not just the first start.
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
        }

        _audioByteOffset = ComputeAudioOffset(TimeSpan.FromSeconds(presentationIndex / _frameRate));

        // Must be set before the request completes; the deferral below may be
        // completed by the worker the moment the lock is released.
        args.Request.SetActualStartPosition(TimeSpan.FromSeconds(presentationIndex / _frameRate));

        lock (_sync)
        {
            // The worker has been pre-decoding from frame 0 since Create, so
            // the initial start at position 0 must not reposition — that would
            // recycle the warm queue and re-decode frame 0 onward (a startup
            // stutter). Real seeks (including back to 0 after playing) still
            // reposition.
            var queueHeadIndex = _videoQueue.Count > 0 ? _videoQueue.Peek().PresentationIndex : -1;
            var redundantStart = Vid1StartupSeekPolicy.IsRedundantStartAtZero(
                presentationIndex,
                repositionPending: _pendingSeekIndex >= 0,
                queueHeadIndex,
                startHandledBefore: _startHandled);
            _startHandled = true;
            if (!redundantStart)
                _pendingSeekIndex = presentationIndex;

            BeginStartingPrerollLocked(args.Request);
            Monitor.PulseAll(_sync);
        }
    }

    /// <summary>
    ///     Pre-roll: holds the Starting handshake open (via its deferral) until
    ///     the queue has <see cref="PrerollFrameCount" /> frames in hand, so the
    ///     first sample requests are served from a warm queue instead of racing
    ///     the decoder frame-by-frame. <see cref="PrerollTimeout" /> bounds the
    ///     wait so short or broken files still start.
    /// </summary>
    private void BeginStartingPrerollLocked(MediaStreamSourceStartingRequest request)
    {
        CompleteStartingDeferralLocked();

        if (_disposed)
            return;

        var repositioning = _pendingSeekIndex >= 0;
        if (!repositioning && (_endOfStream || _videoQueue.Count >= PrerollFrameCount))
            return;

        _startingDeferral = request.GetDeferral();
        var generation = ++_startingDeferralGeneration;
        _startingDeferralTimer = new Timer(
            _ => OnStartingPrerollTimeout(generation),
            null,
            PrerollTimeout,
            Timeout.InfiniteTimeSpan);
    }

    private void OnStartingPrerollTimeout(int generation)
    {
        lock (_sync)
        {
            // A disposed Timer can still have a queued callback; the generation
            // check keeps it from completing a newer handshake early.
            if (generation != _startingDeferralGeneration)
                return;

            CompleteStartingDeferralLocked();
        }
    }

    /// <summary>Worker → pipeline handoff once the pre-roll target is met (or the stream ended).</summary>
    private void TryCompleteStartingPrerollLocked()
    {
        if (_startingDeferral == null)
            return;

        // An unconsumed seek means the queue is about to be recycled — keep
        // holding until frames for the new position land.
        if (_pendingSeekIndex >= 0)
            return;

        if (!_endOfStream && !_disposed && _videoQueue.Count < PrerollFrameCount)
            return;

        CompleteStartingDeferralLocked();
    }

    private void CompleteStartingDeferralLocked()
    {
        _startingDeferralGeneration++;
        _startingDeferralTimer?.Dispose();
        _startingDeferralTimer = null;
        _startingDeferral?.Complete();
        _startingDeferral = null;
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
    ///     Repositions via the provider's anchor-based seek: it resumes from
    ///     the nearest reference-state snapshot / intra frame instead of
    ///     re-decoding from frame 0, and fast-forwards with plane-only decodes.
    ///     A newer seek arriving mid-scan supersedes this one (polled via the
    ///     cancellation callback), so scrubbing stays responsive.
    /// </summary>
    private void Reposition(int targetIndex)
    {
        int resumedAt;
        try
        {
            resumedAt = _provider.SeekToEmissionOrdinal(targetIndex, () =>
            {
                lock (_sync)
                {
                    return _disposed || _pendingSeekIndex >= 0;
                }
            });
        }
        catch
        {
            resumedAt = -1;
        }

        if (resumedAt < 0)
        {
            lock (_sync)
            {
                _endOfStream = true;
                CompletePendingVideoRequestLocked(deliver: true);
                TryCompleteStartingPrerollLocked();
            }

            return;
        }

        _nextDecodeIndex = resumedAt;
        // The decode position moved; the previously captured frame no longer
        // matches the stream position, so the hiccup fallback must re-arm.
        _hasLastGoodFrame = false;
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
            TryCompleteStartingPrerollLocked();
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
