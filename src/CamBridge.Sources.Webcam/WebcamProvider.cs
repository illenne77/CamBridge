using CamBridge.Core;
using CamBridge.Sources.Webcam.Interop;

namespace CamBridge.Sources.Webcam;

/// <summary>
/// Exposes DirectShow video capture devices (UVC webcams) as cameras.
/// </summary>
public sealed class WebcamProvider : ICameraProvider
{
    public const string Id = "webcam";

    private readonly WebcamOptions _options;

    public WebcamProvider(WebcamOptions? options = null)
    {
        _options = options ?? new WebcamOptions();
    }

    public string ProviderId => Id;

    public string VendorName => "Webcam (DirectShow)";

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        // COM enumeration runs on a pool (MTA) thread, never on the caller's STA/UI thread.
        Task.Run<IReadOnlyList<CameraInfo>>(() => DirectShowDevices.Enumerate().Select(CreateInfo).ToList(), cancellationToken);

    public ICamera Create(CameraInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!string.Equals(info.ProviderId, Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Camera '{info.DeviceId}' belongs to provider '{info.ProviderId}'.", nameof(info));
        }

        return new WebcamCamera(info, _options);
    }

    internal static CameraInfo CreateInfo(DirectShowDevice device) =>
        new(Id, device.DeviceId, "Webcam", device.FriendlyName) { Interface = CameraInterface.Webcam };
}
