using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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

    /// <summary>
    /// Runs every consumer's <see cref="IFrameConsumer.WarmUp"/> on blank Mono8 and Bgr8 frames of the
    /// camera's current size, so the first real frames do not wait behind first-call costs. Call after
    /// the camera is configured and before <see cref="Start"/>. Statistics are not affected.
    /// </summary>
    /// <remarks>
    /// Both formats run because the delivered format is a source detail (a Bayer sensor arrives as
    /// Bgr8, for example) that the camera's PixelFormat parameter does not state. The size must be
    /// real: a smaller sample would leave ROIs outside the image and skip the actual processing.
    /// </remarks>
    /// <returns>Per-consumer duration and error, or empty when the camera's size cannot be read.</returns>
    public IReadOnlyList<ConsumerWarmUp> WarmUp()
    {
        ThrowIfRunning();
        if (_runners.Count == 0)
        {
            return [];
        }

        if (!TryReadFrameSize(out int width, out int height, out string? reason))
        {
            LogWarmUpSkipped(_logger, reason);
            return [];
        }

        Frame[] samples = [BlankFrame(width, height, PixelFormat.Mono8), BlankFrame(width, height, PixelFormat.Bgr8)];
        try
        {
            var results = new List<ConsumerWarmUp>(_runners.Count);
            foreach (ConsumerRunner runner in _runners)
            {
                ConsumerWarmUp result = runner.WarmUp(samples);
                LogWarmedUp(_logger, result.Consumer, width, height, result.Duration.TotalMilliseconds);
                results.Add(result);
            }

            return results;
        }
        finally
        {
            foreach (Frame sample in samples)
            {
                sample.Dispose();
            }
        }
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
        _camera.ErrorOccurred += OnCameraError;
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
        _camera.ErrorOccurred -= OnCameraError;
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

    private void OnCameraError(object? sender, CameraErrorEventArgs e)
    {
        if (e.LostFrameId is not long frameId)
        {
            return;
        }

        lock (_frameIdLock)
        {
            // Not above the last frame: the gap before that frame has already counted this one.
            if (frameId > _lastFrameId)
            {
                long firstMissing = _lastFrameId >= 0 ? _lastFrameId + 1 : frameId;
                Interlocked.Add(ref _cameraDropped, frameId - firstMissing + 1);
                _lastFrameId = frameId;
            }
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

    private bool TryReadFrameSize(out int width, out int height, [NotNullWhen(false)] out string? reason)
    {
        width = height = 0;
        try
        {
            width = Convert.ToInt32(_camera.GetParameter(ParameterNames.Width), CultureInfo.InvariantCulture);
            height = Convert.ToInt32(_camera.GetParameter(ParameterNames.Height), CultureInfo.InvariantCulture);
        }
#pragma warning disable CA1031 // Any source may refuse or lack the parameters; warm-up is then skipped, not fatal.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            reason = $"the camera's size could not be read ({ex.Message})";
            return false;
        }

        if (width <= 0 || height <= 0)
        {
            reason = string.Create(CultureInfo.InvariantCulture, $"the camera reports a size of {width}x{height}");
            return false;
        }

        reason = null;
        return true;
    }

    private static Frame BlankFrame(int width, int height, PixelFormat format)
    {
        Frame frame = Frame.Rent(width, height, format, frameId: 0, DateTime.UtcNow);
        frame.GetWritableData().Clear(); // pooled buffers keep old contents; keep the sample deterministic
        return frame;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Frame handling failed on the camera thread")]
    private static partial void LogFrameHandlingFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Warmed up {Consumer} on {Width}x{Height} samples in {Milliseconds:0} ms")]
    private static partial void LogWarmedUp(ILogger logger, string consumer, int width, int height, double milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Consumer warm-up skipped: {Reason}")]
    private static partial void LogWarmUpSkipped(ILogger logger, string reason);
}
