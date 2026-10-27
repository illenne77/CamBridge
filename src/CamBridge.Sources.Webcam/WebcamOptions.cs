using System.Buffers;

namespace CamBridge.Sources.Webcam;

public sealed record WebcamOptions
{
    /// <summary>
    /// How long frame reads may keep failing before the camera reports
    /// <see cref="Core.CameraErrorKind.Disconnected"/> and enters <see cref="Core.CameraState.Faulted"/>.
    /// </summary>
    public TimeSpan DisconnectTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// A frame read slower than this triggers a device presence check before the frame is
    /// published. After unplugging, OpenCV's DirectShow backend keeps returning success about
    /// once per second (its frame wait timeout) instead of failing, so slow reads are the
    /// disconnect signal. A slow read from a present device (e.g. long exposure) costs only one
    /// device enumeration and the frame is still published.
    /// </summary>
    public TimeSpan SlowReadThreshold { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Upper bound for a parameter access to be served by the device thread. A command waits for
    /// at most one frame, so this only trips when the driver hangs.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Pool for frame buffers; <see cref="ArrayPool{T}.Shared"/> when null.</summary>
    public ArrayPool<byte>? BufferPool { get; init; }
}
