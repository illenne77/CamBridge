namespace CamBridge.Core;

/// <summary>
/// Vendor-neutral camera. Implementations wrap one SDK or virtual source.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FrameReceived"/> is raised on the acquisition thread. Handlers must not block
/// (no disk I/O, inspection or UI access): hand the frame to a queue instead. The frame in
/// the event args is valid only during the handler; call <see cref="Frame.Share"/> to keep it.
/// </para>
/// <para>
/// Parameter names follow GenICam SFNC where possible (see <see cref="ParameterNames"/>).
/// Value types: Integer is <see cref="long"/>, Float is <see cref="double"/>, Boolean is
/// <see cref="bool"/>, Enumeration and String are <see cref="string"/>.
/// Unknown names throw <see cref="KeyNotFoundException"/>; parameters that exist but are not
/// currently readable/writable throw <see cref="InvalidOperationException"/>.
/// </para>
/// </remarks>
public interface ICamera : IAsyncDisposable
{
    CameraInfo Info { get; }

    CameraState State { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync(CancellationToken cancellationToken = default);

    Task StartAcquisitionAsync(CancellationToken cancellationToken = default);

    Task StopAcquisitionAsync(CancellationToken cancellationToken = default);

    /// <summary>Exposure time in microseconds (SFNC ExposureTime).</summary>
    double ExposureTimeUs { get; set; }

    /// <summary>Gain in the camera's native unit, typically dB (SFNC Gain).</summary>
    double Gain { get; set; }

    TriggerMode TriggerMode { get; set; }

    /// <summary>Captures one frame when <see cref="TriggerMode"/> is <see cref="TriggerMode.Software"/>.</summary>
    void ExecuteSoftwareTrigger();

    /// <summary>Describes all parameters, including those currently unavailable.</summary>
    IReadOnlyList<CameraParameter> GetParameters();

    object GetParameter(string name);

    void SetParameter(string name, object value);

    /// <summary>Executes a GenICam command node (e.g. "UserSetLoad").</summary>
    void ExecuteCommand(string name);

    /// <summary>Raised on the acquisition thread for every received frame.</summary>
    event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    /// <summary>Raised for acquisition or device errors (e.g. disconnection).</summary>
    event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

    event EventHandler<CameraStateChangedEventArgs>? StateChanged;
}
