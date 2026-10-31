using CamBridge.App.ViewModels;
using CamBridge.Core;

namespace CamBridge.Tests.Windows.App;

public sealed class CoalescingApplierTests
{
    [Fact]
    public async Task Post_WhileApplying_KeepsOnlyLatestValue()
    {
        var applied = new List<int>();
        var gate = new TaskCompletionSource();
        var applier = new CoalescingApplier<int>(async v =>
        {
            applied.Add(v);
            if (v == 1)
            {
                await gate.Task; // first write is "in flight"
            }
        });

        applier.Post(1);
        applier.Post(2);
        applier.Post(3);
        applier.Post(4);
        Assert.True(applier.HasPending);
        gate.SetResult();
        await applier.Idle;

        Assert.Equal([1, 4], applied);
        Assert.False(applier.HasPending);
    }

    [Fact]
    public async Task Post_AfterIdle_StartsNewApply()
    {
        var applied = new List<int>();
        var applier = new CoalescingApplier<int>(v =>
        {
            applied.Add(v);
            return Task.CompletedTask;
        });

        applier.Post(1);
        await applier.Idle;
        applier.Post(2);
        await applier.Idle;

        Assert.Equal([1, 2], applied);
    }
}

public sealed class ParameterValuesTests
{
    [Theory]
    [InlineData(ParameterType.Integer, " 42 ", 42L)]
    [InlineData(ParameterType.Float, "1.5", 1.5)]
    [InlineData(ParameterType.Float, "1e3", 1000.0)]
    [InlineData(ParameterType.Boolean, "true", true)]
    [InlineData(ParameterType.Enumeration, " Software ", "Software")]
    public void Parse_ReturnsCameraValueType(ParameterType type, string text, object expected)
    {
        Assert.Equal(expected, ParameterValues.Parse(type, text));
    }

    [Theory]
    [InlineData(ParameterType.Integer, "1.5")]
    [InlineData(ParameterType.Float, "1,5")] // invariant culture: comma is not a decimal separator
    [InlineData(ParameterType.Boolean, "yes")]
    [InlineData(ParameterType.Command, "")]
    public void Parse_RejectsInvalidText(ParameterType type, string text)
    {
        Assert.Throws<FormatException>(() => ParameterValues.Parse(type, text));
    }

    [Fact]
    public void Format_UsesInvariantCultureAndTrimsDecimals()
    {
        Assert.Equal("15625", ParameterValues.Format(15625.0));
        Assert.Equal("488.281", ParameterValues.Format(488.28125));
        Assert.Equal("-64", ParameterValues.Format(-64L));
        Assert.Equal("Off", ParameterValues.Format("Off"));
        Assert.Equal(string.Empty, ParameterValues.Format(null));
    }

    [Fact]
    public void DescribeRange_ShowsBoundsStepAndEntries()
    {
        Assert.Equal("0 .. 1023", ParameterValues.DescribeRange(new CameraParameter("Focus", ParameterType.Integer) { Minimum = 0, Maximum = 1023, Increment = 1 }));
        Assert.Equal("0 .. 100 (step 5)", ParameterValues.DescribeRange(new CameraParameter("X", ParameterType.Integer) { Minimum = 0, Maximum = 100, Increment = 5 }));
        Assert.Equal("Off / Software", ParameterValues.DescribeRange(new CameraParameter("TriggerMode", ParameterType.Enumeration) { EnumEntries = ["Off", "Software"] }));
        Assert.Equal(string.Empty, ParameterValues.DescribeRange(new CameraParameter("Name", ParameterType.String)));
    }
}
