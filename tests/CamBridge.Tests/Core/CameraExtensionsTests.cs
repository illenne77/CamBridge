using CamBridge.Core;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Core;

public class CameraExtensionsTests
{
    [Fact]
    public void GetParameterOfT_ConvertsIntegerToDouble()
    {
        var camera = new FakeCamera(new CameraInfo("fake", "1", "Fake", "Model"));
        camera.SetParameter(ParameterNames.Width, 640L);

        Assert.Equal(640.0, camera.GetParameter<double>(ParameterNames.Width));
        Assert.Equal(640L, camera.GetParameter<long>(ParameterNames.Width));
    }

    [Fact]
    public void FindParameter_ReturnsNullForUnknownName()
    {
        var camera = new FakeCamera(new CameraInfo("fake", "1", "Fake", "Model"));
        camera.SetParameter(ParameterNames.Gain, 1.0);

        Assert.NotNull(camera.FindParameter(ParameterNames.Gain));
        Assert.Null(camera.FindParameter("NoSuchFeature"));
    }
}
