using CamBridge.Sources.Webcam;

namespace CamBridge.Tests.Windows.Sources;

public sealed class ExposureMappingTests
{
    [Theory]
    [InlineData(-6, 15_625)]
    [InlineData(-11, 488.28125)]
    [InlineData(-2, 250_000)]
    [InlineData(0, 1_000_000)]
    public void ToMicroseconds_IsPowerOfTwoSeconds(int deviceValue, double expected)
    {
        Assert.Equal(expected, ExposureMapping.ToMicroseconds(deviceValue), precision: 6);
    }

    [Theory]
    [InlineData(15_625, -6)]
    [InlineData(20_000, -6)] // log2(0.02 s) = -5.64
    [InlineData(25_000, -5)] // log2(0.025 s) = -5.32
    [InlineData(500, -11)]
    [InlineData(1_000_000, 0)]
    public void ToDeviceValue_RoundsInLogDomain(double microseconds, int expected)
    {
        Assert.Equal(expected, ExposureMapping.ToDeviceValue(microseconds));
    }

    [Fact]
    public void RoundTrip_IsExactForDeviceSteps()
    {
        for (int value = -13; value <= 2; value++)
        {
            Assert.Equal(value, ExposureMapping.ToDeviceValue(ExposureMapping.ToMicroseconds(value)));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToDeviceValue_RejectsNonPositive(double microseconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ExposureMapping.ToDeviceValue(microseconds));
    }
}
