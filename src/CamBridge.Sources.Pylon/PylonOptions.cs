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
    /// GigE heartbeat timeout applied at open, so an unplugged camera is noticed quickly. Null keeps
    /// the camera default. Not applied under a debugger, where pylon raises it to 5 minutes itself
    /// so breakpoints do not drop the connection.
    /// </summary>
    public TimeSpan? HeartbeatTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Highest node visibility listed by <see cref="Core.ICamera.GetParameters"/>; all nodes stay accessible by name.</summary>
    public NodeVisibility MaxVisibility { get; init; } = NodeVisibility.Expert;

    /// <summary>Pool for frame buffers; <see cref="ArrayPool{T}.Shared"/> when null.</summary>
    public ArrayPool<byte>? BufferPool { get; init; }
}
