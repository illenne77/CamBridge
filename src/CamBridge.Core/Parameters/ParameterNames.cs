namespace CamBridge.Core;

/// <summary>
/// GenICam SFNC feature names used across providers. Providers map these to their SDK's
/// own names when they differ (e.g. older devices using "ExposureTimeAbs").
/// </summary>
public static class ParameterNames
{
    public const string ExposureTime = "ExposureTime";
    public const string Gain = "Gain";
    public const string TriggerMode = "TriggerMode";
    public const string TriggerSource = "TriggerSource";
    public const string TriggerSoftware = "TriggerSoftware";
    public const string AcquisitionFrameRate = "AcquisitionFrameRate";
    public const string Width = "Width";
    public const string Height = "Height";
    public const string OffsetX = "OffsetX";
    public const string OffsetY = "OffsetY";
    public const string PixelFormat = "PixelFormat";
}
