using CamBridge.Core;
using CamBridge.Pipeline.Statistics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CamBridge.Pipeline;

/// <summary>
/// Fans frames out from a camera to a live view slot and to consumers, each with its own
/// bounded queue, and keeps acquisition statistics.
/// </summary>
/// <remarks>
/// The camera callback only records counters and shares frame handles (no pixel copies,
/// no blocking except under <see cref="QueueOverflowPolicy.Wait"/>). Starting and stopping
/// camera acquisition stays with the caller; the pipeline only attaches to its events.
/// </remarks>
public sealed partial class AcquisitionPipeline : IAsyncDisposable
{
    private readonly ICamera _camera;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly List<ConsumerRunner> _runners = [];
    private readonly RateMeter _receivedRate;
    private readonly object _frameIdLock = new();
    private long _received;
    private long _cameraDropped;
    private long _lastFrameId = -1;
    private bool _running;

    public AcquisitionPipeline(ICamera camera, TimeProvider? timeProvider = null, ILogger<AcquisitionPipeline>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(camera);

        _camera = camera;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _receivedRate = new RateMeter(_timeProvider);
    }

    /// <summary>Most recent frame for display. The UI takes from it at its own pace.</summary>
    public LatestFrameSlot LiveView { get; } = new();

    public bool IsRunning => _running;

    /// <summary>Adds a consumer. Only allowed while stopped.</summary>
    public void AddConsumer(IFrameConsumer consumer, ConsumerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        options ??= new ConsumerOptions();
        options.Validate();
        ThrowIfRunning();

        _runners.Add(new ConsumerRunner(consumer, options, _timeProvider, _logger));
    }

    /// <summary>Resets statistics, starts consumer workers and attaches to the camera.</summary>
    public void Start()
    {
        ThrowIfRunning();

        Interlocked.Exchange(ref _received, 0);
        Interlocked.Exchange(ref _cameraDropped, 0);
        lock (_frameIdLock)
        {
            _lastFrameId = -1;
        }

        _receivedRate.Reset();

        foreach (ConsumerRunner runner in _runners)
        {
            runner.Start();
        }

        _camera.FrameReceived += OnFrameReceived;
        _running = true;
    }

    /// <summary>
    /// Detaches from the camera and stops consumers.
    /// </summary>
    /// <param name="drain">True to process frames still queued; false to discard them.</param>
    public async Task StopAsync(bool drain = false)
    {
        if (!_running)
        {
            return;
        }

        _camera.FrameReceived -= OnFrameReceived;
        _running = false;

        await Task.WhenAll(_runners.Select(r => r.StopAsync(drain))).ConfigureAwait(false);
    }

    public PipelineStatistics GetStatistics() => new(
        Interlocked.Read(ref _received),
        _receivedRate.GetRate(),
        Interlocked.Read(ref _cameraDropped),
        LiveView.SkippedFrames,
        _runners.Select(r => r.GetStatistics()).ToList());

    public async ValueTask DisposeAsync()
    {
        await StopAsync(drain: false).ConfigureAwait(false);
        foreach (ConsumerRunner runner in _runners)
        {
            runner.Dispose();
        }

        LiveView.Dispose();
    }

    private void OnFrameReceived(object? sender, FrameReceivedEventArgs e)
    {
        try
        {
            Frame frame = e.Frame;
            Interlocked.Increment(ref _received);
            _receivedRate.Record();
            TrackFrameId(frame.FrameId);

            LiveView.Publish(frame.Share());
            foreach (ConsumerRunner runner in _runners)
            {
                if (runner.Accepts(frame))
                {
                    runner.Enqueue(frame.Share());
                }
            }
        }
#pragma warning disable CA1031 // Never let an exception escape into the vendor SDK's acquisition thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFrameHandlingFailed(_logger, ex);
        }
    }

    private void TrackFrameId(long frameId)
    {
        lock (_frameIdLock)
        {
            // A FrameId at or below the last one means the camera restarted its counter.
            if (_lastFrameId >= 0 && frameId > _lastFrameId + 1)
            {
                Interlocked.Add(ref _cameraDropped, frameId - _lastFrameId - 1);
            }

            _lastFrameId = frameId;
        }
    }

    private void ThrowIfRunning()
    {
        if (_running)
        {
            throw new InvalidOperationException("The pipeline is running. Stop it first.");
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Frame handling failed on the camera thread")]
    private static partial void LogFrameHandlingFailed(ILogger logger, Exception exception);
}
