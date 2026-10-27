using System.Buffers;

namespace CamBridge.Sources.ImageFolder;

public sealed record ImageFolderOptions
{
    /// <summary>
    /// Upper bound for decoded images held in memory. Images are decoded once at open so
    /// playback runs at a steady frame rate without disk I/O on the acquisition thread.
    /// </summary>
    public long MaxMemoryBytes { get; init; } = 1L << 30;

    /// <summary>Pool for frame buffers; <see cref="ArrayPool{T}.Shared"/> when null.</summary>
    public ArrayPool<byte>? BufferPool { get; init; }
}
