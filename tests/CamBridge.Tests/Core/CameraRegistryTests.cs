using CamBridge.Core;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Core;

public class CameraRegistryTests
{
    [Fact]
    public async Task EnumerateAsync_MergesProvidersInRegistrationOrder()
    {
        var registry = new CameraRegistry([
            new FakeCameraProvider("b", "b1", "b2"),
            new FakeCameraProvider("a", "a1"),
        ]);

        CameraEnumerationResult result = await registry.EnumerateAsync();

        Assert.Equal(["b1", "b2", "a1"], result.Cameras.Select(c => c.DeviceId));
        Assert.Empty(result.Failures);
    }

    [Fact]
    public async Task EnumerateAsync_IsolatesFailingProvider()
    {
        var registry = new CameraRegistry([
            new FakeCameraProvider("broken", _ => throw new InvalidOperationException("SDK missing")),
            new FakeCameraProvider("faulted", _ => Task.FromException<IReadOnlyList<CameraInfo>>(new IOException("timeout"))),
            new FakeCameraProvider("ok", "cam1"),
        ]);

        CameraEnumerationResult result = await registry.EnumerateAsync();

        Assert.Equal("cam1", Assert.Single(result.Cameras).DeviceId);
        Assert.Collection(
            result.Failures,
            f => { Assert.Equal("broken", f.ProviderId); Assert.IsType<InvalidOperationException>(f.Exception); },
            f => { Assert.Equal("faulted", f.ProviderId); Assert.IsType<IOException>(f.Exception); });
    }

    [Fact]
    public async Task EnumerateAsync_RejectsCamerasWithForeignProviderId()
    {
        var registry = new CameraRegistry([
            new FakeCameraProvider("liar", _ => Task.FromResult<IReadOnlyList<CameraInfo>>(
                [new CameraInfo("other", "x", "Fake", "Model")])),
        ]);

        CameraEnumerationResult result = await registry.EnumerateAsync();

        Assert.Empty(result.Cameras);
        Assert.Equal("liar", Assert.Single(result.Failures).ProviderId);
    }

    [Fact]
    public async Task EnumerateAsync_RunsProvidersConcurrently()
    {
        var gate = new TaskCompletionSource();
        int started = 0;

        async Task<IReadOnlyList<CameraInfo>> Slow(string id, CancellationToken ct)
        {
            if (Interlocked.Increment(ref started) == 2)
            {
                gate.SetResult();
            }

            await gate.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            return [new CameraInfo(id, id + "1", "Fake", "Model")];
        }

        var registry = new CameraRegistry([
            new FakeCameraProvider("p1", ct => Slow("p1", ct)),
            new FakeCameraProvider("p2", ct => Slow("p2", ct)),
        ]);

        CameraEnumerationResult result = await registry.EnumerateAsync();

        Assert.Equal(2, result.Cameras.Count);
    }

    [Fact]
    public async Task EnumerateAsync_PropagatesCallerCancellation()
    {
        using var cts = new CancellationTokenSource();
        var registry = new CameraRegistry([
            new FakeCameraProvider("slow", async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            }),
        ]);

        Task<CameraEnumerationResult> task = registry.EnumerateAsync(cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task Create_RoutesToOwningProvider()
    {
        var a = new FakeCameraProvider("a", "a1");
        var b = new FakeCameraProvider("b", "b1");
        var registry = new CameraRegistry([a, b]);
        CameraInfo info = (await registry.EnumerateAsync()).Cameras.Single(c => c.DeviceId == "b1");

        await using ICamera camera = registry.Create(info);

        Assert.Same(info, camera.Info);
        Assert.Empty(a.Created);
        Assert.Single(b.Created);
    }

    [Fact]
    public void Create_UnknownProvider_Throws()
    {
        var registry = new CameraRegistry([new FakeCameraProvider("a")]);

        Assert.Throws<ArgumentException>(() => registry.Create(new CameraInfo("missing", "x", "Fake", "Model")));
    }

    [Fact]
    public void Constructor_RejectsDuplicateProviderIds()
    {
        Assert.Throws<ArgumentException>(() => new CameraRegistry([
            new FakeCameraProvider("dup"),
            new FakeCameraProvider("DUP"),
        ]));
    }
}
