using CamBridge.Sources.Webcam;

namespace CamBridge.Tests.Windows.Sources;

public sealed class DeviceThreadTests : IDisposable
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    private readonly DeviceThread _device = new("test device", TimeSpan.FromSeconds(5));

    public void Dispose() => _device.Dispose();

    [Fact]
    public void Invoke_RunsOnDeviceThreadAndReturnsResult()
    {
        (bool onDevice, int id) = _device.Invoke(() => (_device.IsCurrentThread, Environment.CurrentManagedThreadId));

        Assert.True(onDevice);
        Assert.NotEqual(Environment.CurrentManagedThreadId, id);
        Assert.False(_device.IsCurrentThread);
    }

    [Fact]
    public void Invoke_RethrowsOriginalException()
    {
        var ex = Assert.Throws<KeyNotFoundException>(() => _device.Invoke(() => throw new KeyNotFoundException("missing")));
        Assert.Equal("missing", ex.Message);
    }

    [Fact]
    public void Invoke_FromDeviceThread_RunsInline()
    {
        int nested = _device.Invoke(() => _device.Invoke(() => 42));

        Assert.Equal(42, nested);
    }

    [Fact]
    public async Task Pump_RepeatsAndCommandsRunBetweenCalls()
    {
        int pumped = 0;
        await _device.InvokeAsync(() => _device.Pump = () =>
        {
            Interlocked.Increment(ref pumped);
            Thread.Sleep(1);
        });

        await WaitForAsync(() => Volatile.Read(ref pumped) >= 5);
        int duringPump = _device.Invoke(() => Volatile.Read(ref pumped));
        await _device.InvokeAsync(() => _device.Pump = null);
        int stopped = Volatile.Read(ref pumped);
        await Task.Delay(50);

        Assert.True(duringPump >= 5);
        Assert.Equal(stopped, Volatile.Read(ref pumped));
    }

    [Fact]
    public async Task Pump_FailureRemovesPumpAndIsReported()
    {
        var failure = new TaskCompletionSource<Exception>();
        _device.PumpFailed += failure.SetResult;

        await _device.InvokeAsync(() => _device.Pump = () => throw new InvalidOperationException("grab failed"));
        Exception reported = await failure.Task.WaitAsync(TestTimeout);

        Assert.Equal("grab failed", reported.Message);
        Assert.Null(_device.Invoke(() => _device.Pump));
    }

    [Fact]
    public void Pump_CannotBeSetFromOtherThreads()
    {
        Assert.Throws<InvalidOperationException>(() => _device.Pump = () => { });
    }

    [Fact]
    public async Task Dispose_RunsQueuedCommandsThenRejectsNewOnes()
    {
        var device = new DeviceThread("disposing", TimeSpan.FromSeconds(5));
        _ = device.InvokeAsync(() => Thread.Sleep(50));
        Task<int> queued = device.InvokeAsync(() => 7);

        device.Dispose();

        Assert.True(queued.IsCompletedSuccessfully);
        Assert.Equal(7, await queued);
        Assert.Throws<ObjectDisposedException>(() => { _ = device.InvokeAsync(() => 1); });
        device.Dispose(); // idempotent
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(TestTimeout);
        while (!condition())
        {
            await Task.Delay(5, cts.Token);
        }
    }
}
