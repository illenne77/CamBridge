using CamBridge.Core;

namespace CamBridge.Tests.Fakes;

internal sealed class FakeCameraProvider(string providerId, Func<CancellationToken, Task<IReadOnlyList<CameraInfo>>> enumerate)
    : ICameraProvider
{
    public FakeCameraProvider(string providerId, params string[] deviceIds)
        : this(providerId, _ => Task.FromResult<IReadOnlyList<CameraInfo>>(
            deviceIds.Select(id => new CameraInfo(providerId, id, "Fake", "Model")).ToList()))
    {
    }

    public string ProviderId { get; } = providerId;

    public string VendorName => "Fake " + ProviderId;

    public List<CameraInfo> Created { get; } = [];

    public Task<IReadOnlyList<CameraInfo>> EnumerateAsync(CancellationToken cancellationToken = default) =>
        enumerate(cancellationToken);

    public ICamera Create(CameraInfo info)
    {
        Created.Add(info);
        return new FakeCamera(info);
    }
}
