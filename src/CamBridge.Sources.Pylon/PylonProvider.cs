#if HAS_PYLON
using Basler.Pylon;
using CamBridge.Core;
using CoreCameraInfo = CamBridge.Core.CameraInfo;
using ICamera = CamBridge.Core.ICamera;

namespace CamBridge.Sources.Pylon;

/// <summary>
/// Basler cameras (GigE, USB3 and the pylon camera emulator) through the pylon .NET API.
/// Emulated cameras appear when the PYLON_CAMEMU environment variable is set (e.g. 2).
/// </summary>
internal sealed class PylonProvider(PylonOptions options) : ICameraProvider
{
    public string ProviderId => PylonProviderFactory.ProviderId;

    public string VendorName => "Basler pylon";

    public Task<IReadOnlyList<CoreCameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        // GigE discovery takes up to a second; never on the caller's (UI) thread.
        Task.Run<IReadOnlyList<CoreCameraInfo>>(() =>
        {
            try
            {
                return CameraFinder.Enumerate().Select(CreateInfo).ToList();
            }
            catch (Exception ex) when (ex is DllNotFoundException or TypeInitializationException)
            {
                throw new InvalidOperationException(
                    "The pylon runtime could not be loaded. Check that <pylon>\\Runtime\\x64 is on PATH (restart the app after installing pylon).", ex);
            }
        }, cancellationToken);

    public ICamera Create(CoreCameraInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (!string.Equals(info.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"Camera '{info.DeviceId}' belongs to provider '{info.ProviderId}'.", nameof(info));
        }

        return new PylonCamera(info, options);
    }

    /// <summary>The pylon full name is the device's unique key and is what <see cref="PylonCamera"/> opens.</summary>
    internal static CoreCameraInfo CreateInfo(ICameraInfo pylon)
    {
        string Get(string key) => pylon.ContainsKey(key) ? pylon[key] ?? string.Empty : string.Empty;

        string deviceType = Get(CameraInfoKey.DeviceType);
        string userName = Get(CameraInfoKey.UserDefinedName);
        string serial = Get(CameraInfoKey.SerialNumber);
        return new CoreCameraInfo(PylonProviderFactory.ProviderId, Get(CameraInfoKey.FullName), Get(CameraInfoKey.VendorName), Get(CameraInfoKey.ModelName))
        {
            SerialNumber = serial.Length == 0 ? null : serial,
            UserDefinedName = userName.Length == 0 ? null : userName,
            Interface = InterfaceOf(deviceType),
        };
    }

    internal static CameraInterface InterfaceOf(string deviceType) => deviceType switch
    {
        _ when deviceType.Contains("GigE", StringComparison.OrdinalIgnoreCase) => CameraInterface.GigE,
        _ when deviceType.Contains("Usb", StringComparison.OrdinalIgnoreCase) => CameraInterface.Usb3,
        _ when deviceType.Contains("Emu", StringComparison.OrdinalIgnoreCase) => CameraInterface.Emulated,
        _ => CameraInterface.Unknown,
    };
}
#endif
