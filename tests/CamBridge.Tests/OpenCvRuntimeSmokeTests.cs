using OpenCvSharp;

namespace CamBridge.Tests;

/// <summary>
/// Verifies that the OS-specific OpenCV native runtime is resolved for the test host.
/// </summary>
public class OpenCvRuntimeSmokeTests
{
    [Fact]
    public void NativeRuntime_Loads_And_ProcessesMat()
    {
        using var src = new Mat(4, 4, MatType.CV_8UC1, Scalar.All(200));
        using var dst = new Mat();

        Cv2.Threshold(src, dst, 128, 255, ThresholdTypes.Binary);

        Assert.Equal(16, Cv2.CountNonZero(dst));
    }
}
