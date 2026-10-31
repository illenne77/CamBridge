using CamBridge.Core;

namespace CamBridge.Tests.Fakes;

/// <summary>
/// Minimal camera. Tests push frames with <see cref="Emit"/>, which follows the camera contract:
/// the frame is valid only during the event and disposed afterwards.
/// </summary>
internal sealed class FakeCamera(CameraInfo info) : ICamera
{
    private readonly Dictionary<string, object> _values = new(StringComparer.Ordinal);

    public CameraInfo Info { get; } = info;

    public CameraState State => CameraState.Closed;

    public double ExposureTimeUs { get; set; }

    public double Gain { get; set; }

    public TriggerMode TriggerMode { get; set; }

    public event EventHandler<FrameReceivedEventArgs>? FrameReceived;

    public event EventHandler<CameraErrorEventArgs>? ErrorOccurred;

#pragma warning disable CS0067 // Part of the contract but never raised by this fake.
    public event EventHandler<CameraStateChangedEventArgs>? StateChanged;
#pragma warning restore CS0067

    public void RaiseError(CameraErrorEventArgs error) => ErrorOccurred?.Invoke(this, error);

    public void Emit(Frame frame)
    {
        try
        {
            FrameReceived?.Invoke(this, new FrameReceivedEventArgs(frame));
        }
        finally
        {
            frame.Dispose();
        }
    }

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
