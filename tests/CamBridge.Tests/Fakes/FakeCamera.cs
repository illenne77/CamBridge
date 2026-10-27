using CamBridge.Core;

namespace CamBridge.Tests.Fakes;

/// <summary>Minimal camera for registry tests; acquisition members are not exercised.</summary>
internal sealed class FakeCamera(CameraInfo info) : ICamera
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public CameraInfo Info { get; } = info;

    public CameraState State => CameraState.Closed;

    public double ExposureTimeUs { get; set; }

    public double Gain { get; set; }

    public TriggerMode TriggerMode { get; set; }

#pragma warning disable CS0067 // Events are part of the contract but never raised by this fake.
    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

    public event EventHandler<CameraStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

    public Task OpenAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StartAcquisitionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task StopAcquisitionAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public void ExecuteSoftwareTrigger()
    {
    }

    public IReadOnlyList<CameraParameter> GetParameters() =>
        _values.Keys.Select(k => new CameraParameter(k, ParameterType.Integer) { IsWritable = true }).ToList();

    public object GetParameter(string name) =>
        _values.TryGetValue(name, out object? value) ? value : throw new KeyNotFoundException(name);

    public void SetParameter(string name, object value) => _values[name] = value;

    public void ExecuteCommand(string name)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
