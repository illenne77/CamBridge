using CamBridge.Core;
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
    private readonly ILogger _logger;

    private CameraSession(ICamera camera, AcquisitionPipeline pipeline, ILogger logger)
    {
        Camera = camera;
        Pipeline = pipeline;
        _logger = logger;
        camera.ErrorOccurred += OnErrorOccurred;
        camera.StateChanged += OnStateChanged;
    }

    public ICamera Camera { get; }

    public AcquisitionPipeline Pipeline { get; }

    public CameraState State => Camera.State;

    /// <summary>Raised on the thread that changed the state (often a camera thread).</summary>
    public event EventHandler<CameraStateChangedEventArgs>? StateChanged;

    /// <summary>Creates the camera and opens it. The camera is disposed if opening fails.</summary>
    public static async Task<CameraSession> OpenAsync(CameraRegistry registry, CameraInfo info, ILoggerFactory loggerFactory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ICamera camera = registry.Create(info);
        try
        {
            await camera.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await camera.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var pipeline = new AcquisitionPipeline(camera, logger: loggerFactory.CreateLogger<AcquisitionPipeline>());
        var session = new CameraSession(camera, pipeline, loggerFactory.CreateLogger<CameraSession>());
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
            await Camera.DisposeAsync().ConfigureAwait(false);
            LogClosed(_logger, Camera.Info.DisplayName);
        }
    }

    private void OnErrorOccurred(object? sender, CameraErrorEventArgs e)
    {
        if (e.Kind == CameraErrorKind.FrameError)
        {
            LogFrameError(_logger, e.Exception, e.Message);
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Message}")]
    private static partial void LogFrameError(ILogger logger, Exception? exception, string message);
}
