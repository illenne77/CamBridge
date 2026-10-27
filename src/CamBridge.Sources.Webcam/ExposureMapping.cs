namespace CamBridge.Sources.Webcam;

/// <summary>
/// Converts between SFNC ExposureTime (microseconds) and the DirectShow CameraControl_Exposure
/// value, which is log2 of the exposure in seconds (e.g. -6 = 1/64 s = 15625 us).
/// </summary>
public static class ExposureMapping
{
    private const double MicrosecondsPerSecond = 1_000_000;

    public static double ToMicroseconds(int deviceValue) => MicrosecondsPerSecond * Math.Pow(2, deviceValue);

    /// <summary>Nearest device step for <paramref name="microseconds"/> (rounded in the log domain).</summary>
    public static int ToDeviceValue(double microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(microseconds);
        return (int)Math.Round(Math.Log2(microseconds / MicrosecondsPerSecond), MidpointRounding.AwayFromZero);
    }
}
