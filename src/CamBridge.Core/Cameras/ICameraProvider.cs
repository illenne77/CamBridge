namespace CamBridge.Core;

/// <summary>
/// Discovers and creates cameras of one vendor SDK or virtual source.
/// </summary>
public interface ICameraProvider
{
    /// <summary>Stable, unique id (e.g. "pylon", "webcam"). Stored in <see cref="CameraInfo.ProviderId"/>.</summary>
    string ProviderId { get; }

    /// <summary>Human-readable vendor or source name for the UI.</summary>
    string VendorName { get; }

    /// <summary>
    /// Lists available cameras. May take seconds for network transports (GigE discovery),
    /// hence asynchronous.
    /// </summary>
    Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a closed camera for an entry returned by <see cref="EnumerateAsync"/>.</summary>
    ICamera Create(CameraInfo info);
}
