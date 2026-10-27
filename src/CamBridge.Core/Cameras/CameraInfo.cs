namespace CamBridge.Core;

/// <summary>
/// Identifies one camera as reported by a provider during enumeration.
/// </summary>
/// <param name="ProviderId">Id of the <see cref="ICameraProvider"/> that can create this camera.</param>
/// <param name="DeviceId">Provider-specific unique key used to open the device.</param>
public sealed record CameraInfo(string ProviderId, string DeviceId, string Vendor, string Model)
{
    public string? SerialNumber { get; init; }

    public string? UserDefinedName { get; init; }

    public CameraInterface Interface { get; init; } = CameraInterface.Unknown;

    public string DisplayName =>
        !string.IsNullOrWhiteSpace(UserDefinedName) ? $"{UserDefinedName} ({Vendor} {Model})"
        : !string.IsNullOrWhiteSpace(SerialNumber) ? $"{Vendor} {Model} ({SerialNumber})"
        : $"{Vendor} {Model}";
}
