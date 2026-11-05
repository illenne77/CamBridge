using System.Buffers;

namespace CamBridge.Sources.Pylon;

/// <summary>GenICam node visibility levels, lowest first.</summary>
public enum NodeVisibility
{
    Beginner,
    Expert,
    Guru,
}

public sealed record PylonOptions
{
    /// <summary>Number of pylon grab buffers (MaxNumBuffer); more buffers ride out short host stalls.</summary>
    public int BufferCount { get; init; } = 16;

    /// <summary>
    /// Negotiates the largest packet size the network path carries each time a GigE stream starts.
    /// Otherwise the camera keeps its stored size, which can be far from what this NIC allows.
    /// Setting GevSCPSPacketSize by name turns negotiation off until the camera is opened again.
    /// </summary>
    public bool AutoPacketSize { get; init; } = true;

    /// <summary>
    /// GigE heartbeat timeout applied at open, so an unplugged camera is noticed quickly. Null keeps
    /// the camera default. Not applied under a debugger, where pylon raises it to 5 minutes itself
    /// so breakpoints do not drop the connection.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Highest node visibility listed by <see cref="Core.ICamera.GetParameters"/>; all nodes stay accessible by name.</summary>
    public NodeVisibility MaxVisibility { get; init; } = NodeVisibility.Expert;

    /// <summary>Pool for frame buffers; <see cref="ArrayPool{T}.Shared"/> when null.</summary>
    public ArrayPool<byte>? BufferPool { get; init; }

    /// <summary>
    /// Replaces a pixel format this source cannot deliver (e.g. Mono12) with Mono8 when the camera opens, so frames
    /// arrive. Turn off to keep the camera exactly as it was set, e.g. to measure transport under another
    /// program's settings with <see cref="DeliverFrames"/> off.
    /// </summary>
    public bool ReplaceUnsupportedPixelFormat { get; init; } = true;

    /// <summary>
    /// Copies (and converts) each grab into a <see cref="Core.Frame"/> and raises <see cref="Core.ICamera.FrameReceived"/>.
    /// Turn off when only <see cref="GrabObserver"/> is wanted: no pixel work is then done on the grab thread and any
    /// pixel format can be counted.
    /// </summary>
    public bool DeliverFrames { get; init; } = true;

    /// <summary>
    /// Called on pylon's grab thread for every grab result, failed or not, before any copy or conversion. It must not
    /// block or allocate much: it delays the next result as directly as a slow frame handler does. Exceptions it
    /// throws are reported through <see cref="Core.ICamera.ErrorOccurred"/> and do not stop acquisition.
    /// </summary>
    public Action<PylonGrab>? GrabObserver { get; init; }
}
