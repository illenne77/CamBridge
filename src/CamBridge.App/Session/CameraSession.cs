using CamBridge.App.Logging;
using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.Pipeline;
using Microsoft.Extensions.Logging;

namespace CamBridge.App.Session;

/// <summary>
/// One connected camera with its acquisition pipeline. Owns both and keeps their start/stop
/// order: the pipeline attaches before the camera streams and detaches after it stops, so
/// no frame is missed or delivered to a stopped pipeline.
/// </summary>
public sealed partial class CameraSession : IAsyncDisposable
{
    /// <summary>Inspection queue: a slow recipe drops old frames instead of lagging behind the camera.</summary>
    private static readonly ConsumerOptions InspectionQueue = new() { Capacity = 2, OverflowPolicy = QueueOverflowPolicy.DropOldest };

    private readonly ILogger _logger;
    private readonly InspectionEngine? _initialEngine;
    private readonly LogThrottle _frameErrorLog = new(TimeSpan.FromSeconds(1));

    private CameraSession(ICamera camera, AcquisitionPipeline pipeline, InspectionEngine? engine, ILogger logger)
    {
        Camera = camera;
        Pipeline = pipeline;
        _logger = logger;
        _initialEngine = engine;
        if (engine is not null)
        {
            Inspection = new InspectionConsumer(engine);
            pipeline.AddConsumer(Inspection, InspectionQueue);
        }

        camera.ErrorOccurred += OnErrorOccurred;
        camera.StateChanged += OnStateChanged;
    }

    public ICamera Camera { get; }

    public AcquisitionPipeline Pipeline { get; }

    /// <summary>Inspection consumer, or null when the session was opened without a recipe.</summary>
    public InspectionConsumer? Inspection { get; }

    public CameraState State => Camera.State;

    /// <summary>Raised on the thread that changed the state (often a camera thread).</summary>
    public event EventHandler<CameraStateChangedEventArgs>? StateChanged;

    /// <summary>
    /// Creates the camera and opens it. The session takes ownership of <paramref name="engine"/>
    /// (disposed with the session, or right away if opening fails).
    /// </summary>
    public static async Task<CameraSession> OpenAsync(
        CameraRegistry registry, CameraInfo info, InspectionEngine? engine, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ICamera camera;
        try
        {
            camera = registry.Create(info);
        }
        catch
        {
            engine?.Dispose();
            throw;
        }

        try
        {
            await camera.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            engine?.Dispose();
            await camera.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var pipeline = new AcquisitionPipeline(camera, logger: loggerFactory.CreateLogger<AcquisitionPipeline>());
        var session = new CameraSession(camera, pipeline, engine, loggerFactory.CreateLogger<CameraSession>());
        LogOpened(session._logger, info.DisplayName, info.ProviderId);
        return session;
    }

    public async Task StartAsync()
    {
        Pipeline.Start();
        try
        {
            await Camera.StartAcquisitionAsync().ConfigureAwait(false);
        }
        catch
        {
            await Pipeline.StopAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task StopAsync()
    {
        await Camera.StopAcquisitionAsync().ConfigureAwait(false);
        await Pipeline.StopAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await StopAsync().ConfigureAwait(false);
            await Camera.CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            Camera.ErrorOccurred -= OnErrorOccurred;
            Camera.StateChanged -= OnStateChanged;
            await Pipeline.DisposeAsync().ConfigureAwait(false);
            Inspection?.Dispose(); // releases engines swapped in since; the initial one is ours
            _initialEngine?.Dispose();
            await Camera.DisposeAsync().ConfigureAwait(false);
            LogClosed(_logger, Camera.Info.DisplayName);
        }
    }

    private void OnErrorOccurred(object? sender, CameraErrorEventArgs e)
    {
        if (e.Kind == CameraErrorKind.FrameError)
        {
            // A bad link can fail every frame. The drop counter has the count, so the log needs only a sample.
            if (_frameErrorLog.ShouldLog(out long skipped))
            {
                LogFrameError(_logger, e.Exception, e.Message, skipped);
            }
        }
        else
        {
            LogCameraError(_logger, e.Exception, e.Kind, e.Message);
        }
    }

    private void OnStateChanged(object? sender, CameraStateChangedEventArgs e)
    {
        LogStateChanged(_logger, Camera.Info.DisplayName, e.OldState, e.NewState);
        StateChanged?.Invoke(this, e);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened {Camera} ({Provider})")]
    private static partial void LogOpened(ILogger logger, string camera, string provider);

    [LoggerMessage(Level = LogLevel.Information, Message = "Closed {Camera}")]
    private static partial void LogClosed(ILogger logger, string camera);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Camera}: {OldState} -> {NewState}")]
    private static partial void LogStateChanged(ILogger logger, string camera, CameraState oldState, CameraState newState);

    [LoggerMessage(Level = LogLevel.Error, Message = "Camera error {Kind}: {Message}")]
    private static partial void LogCameraError(ILogger logger, Exception? exception, CameraErrorKind kind, string message);

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message} (+{Skipped} more since the last line)")]
    private static partial void LogFrameError(ILogger logger, Exception? exception, string message, long skipped);
}
