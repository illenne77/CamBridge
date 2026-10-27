using CamBridge.Core;
using CamBridge.Sources.Pylon;

namespace CamBridge.Tests.Windows.Sources;

/// <summary>SDK-independent pylon logic: runs on every machine.</summary>
public sealed class FeatureNamesTests
{
    private static readonly HashSet<string> Sfnc2 = ["ExposureTime", "ExposureTimeAbs", "Gain", "GainRaw", "AcquisitionFrameRate", "Width"];
    private static readonly HashSet<string> Sfnc1 = ["ExposureTimeAbs", "GainRaw", "AcquisitionFrameRateAbs", "Width"];

    [Theory]
    [InlineData(ParameterNames.ExposureTime, "ExposureTime", "ExposureTimeAbs")]
    [InlineData(ParameterNames.Gain, "Gain", "GainRaw")]
    [InlineData(ParameterNames.AcquisitionFrameRate, "AcquisitionFrameRate", "AcquisitionFrameRateAbs")]
    [InlineData(ParameterNames.Width, "Width", "Width")]
    public void Resolve_PrefersSfnc2AndFallsBackToSfnc1(string name, string onSfnc2, string onSfnc1)
    {
        Assert.Equal(onSfnc2, FeatureNames.Resolve(name, Sfnc2.Contains));
        Assert.Equal(onSfnc1, FeatureNames.Resolve(name, Sfnc1.Contains));
    }

    [Fact]
    public void Resolve_MissingNode_IsNull()
    {
        Assert.Null(FeatureNames.Resolve(ParameterNames.Gain, _ => false));
        Assert.Null(FeatureNames.Resolve("NoSuchFeature", Sfnc2.Contains));
    }

    [Fact]
    public void UnitOf_PrefersDescribedUnitThenKnownUnits()
    {
        Assert.Equal("us", FeatureNames.UnitOf("ExposureTimeAbs", null));
        Assert.Equal("raw", FeatureNames.UnitOf("GainRaw", string.Empty));
        Assert.Equal("ms", FeatureNames.UnitOf("ExposureTime", "ms"));
        Assert.Null(FeatureNames.UnitOf("Gamma", null));
    }
}

public sealed class BlockIdUnwrapperTests
{
    [Fact]
    public void GigE_WrapAt65535_StaysContiguous()
    {
        var ids = new BlockIdUnwrapper(sixteenBitWrap: true);

        long[] result = [ids.Next(65534), ids.Next(65535), ids.Next(1), ids.Next(2)];

        Assert.Equal([65534L, 65535L, 65536L, 65537L], result);
    }

    [Fact]
    public void GigE_LossAcrossWrap_KeepsTheGap()
    {
        var ids = new BlockIdUnwrapper(sixteenBitWrap: true);

        long before = ids.Next(65534);
        long after = ids.Next(2); // 65535 and 1 were lost

        Assert.Equal(3, after - before);
    }

    [Fact]
    public void GigE_SmallStepBack_IsNotAWrap()
    {
        // Out-of-order or restarted IDs must not jump by a whole period.
        var ids = new BlockIdUnwrapper(sixteenBitWrap: true);
        ids.Next(500);

        Assert.Equal(490, ids.Next(490));
    }

    [Fact]
    public void GigE_WithoutBlockIdSupport_UsesHostCounter()
    {
        var ids = new BlockIdUnwrapper(sixteenBitWrap: true);

        Assert.Equal([1L, 2L, 3L], [ids.Next(0), ids.Next(0), ids.Next(0)]);
    }

    [Fact]
    public void Usb_PassesThrough64BitIds()
    {
        var ids = new BlockIdUnwrapper(sixteenBitWrap: false);

        Assert.Equal([0L, 1L, 65535L, 65536L, 70000L], [ids.Next(0), ids.Next(1), ids.Next(65535), ids.Next(65536), ids.Next(70000)]);
    }
}
