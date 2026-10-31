using System.Buffers;

namespace CamBridge.Core;

/// <summary>
/// One image from a camera, backed by a pooled buffer.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Frame"/> is a handle to a reference-counted buffer. Each handle must be
/// disposed exactly once by whoever holds it; disposing a handle twice is harmless.
/// The buffer returns to its pool when the last handle is disposed.
/// </para>
/// <para>
/// To keep a frame beyond the scope it was handed to you in (for example past a
/// <see cref="ICamera.FrameReceived"/> handler), call <see cref="Share"/> and dispose the
/// returned handle when done. Sharing does not copy pixel data.
/// </para>
/// </remarks>
public sealed class Frame : IDisposable
{
    private FrameBuffer? _buffer;

    private Frame(FrameBuffer buffer, Frame metadataSource)
        : this(buffer, metadataSource.Width, metadataSource.Height, metadataSource.Stride,
               metadataSource.PixelFormat, metadataSource.FrameId, metadataSource.TimestampUtc,
               metadataSource.DeviceTimestamp)
    {
    }

    private Frame(FrameBuffer buffer, int width, int height, int stride, PixelFormat pixelFormat,
                  long frameId, DateTime timestampUtc, long deviceTimestamp)
    {
        _buffer = buffer;
        Width = width;
        Height = height;
        Stride = stride;
        PixelFormat = pixelFormat;
        FrameId = frameId;
        TimestampUtc = timestampUtc;
        DeviceTimestamp = deviceTimestamp;
    }

    /// <summary>Device-side frame counter (GenICam block ID). Gaps indicate dropped frames.</summary>
    public long FrameId { get; }

    /// <summary>Host time at which the frame was received.</summary>
    public DateTime TimestampUtc { get; }

    /// <summary>Device timestamp in vendor-specific ticks, or 0 when the source has none.</summary>
    public long DeviceTimestamp { get; }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Bytes per row, including any padding.</summary>
    public int Stride { get; }

    public PixelFormat PixelFormat { get; }

    /// <summary>Size of the pixel data in bytes (<see cref="Stride"/> x <see cref="Height"/>).</summary>
    public int Length => Stride * Height;

    /// <summary>True once this handle has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _buffer) is null;

    /// <summary>Pixel data. Throws <see cref="ObjectDisposedException"/> after disposal.</summary>
    public ReadOnlySpan<byte> Data => GetBuffer().Array.AsSpan(0, Length);

    /// <summary>
    /// Pixel data as memory, for APIs that need to pin (e.g. wrapping in an OpenCV Mat).
    /// Valid only while this handle is not disposed.
    /// </summary>
    public ReadOnlyMemory<byte> Memory => GetBuffer().Array.AsMemory(0, Length);

    /// <summary>
    /// Rents a buffer for a new frame. The caller owns the returned handle and fills it
    /// through <see cref="GetWritableData"/> before publishing it.
    /// </summary>
    /// <param name="stride">Bytes per row; 0 means tightly packed.</param>
    /// <param name="pool">Buffer pool; <see cref="ArrayPool{T}.Shared"/> when null.</param>
    public static Frame Rent(
        int width,
        int height,
        PixelFormat pixelFormat,
        long frameId,
        DateTime timestampUtc,
        long deviceTimestamp = 0,
        int stride = 0,
        ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfNegative(stride);

        long minStride = (long)width * pixelFormat.BytesPerPixel();
        if (stride == 0)
        {
            stride = checked((int)minStride);
        }
        else if (stride < minStride)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, $"Stride must be at least {minStride} bytes.");
        }

        int length = checked(stride * height);
        var buffer = new FrameBuffer(pool ?? ArrayPool<byte>.Shared, length);
        return new Frame(buffer, width, height, stride, pixelFormat, frameId, timestampUtc, deviceTimestamp);
    }

    /// <summary>
    /// Writable pixel data for the producer that rented the frame. Throws once the frame
    /// has been shared, because other holders assume the data is immutable.
    /// </summary>
    public Span<byte> GetWritableData()
    {
        FrameBuffer buffer = GetBuffer();
        if (buffer.RefCount > 1)
        {
            throw new InvalidOperationException("A shared frame is read-only.");
        }

        return buffer.Array.AsSpan(0, Length);
    }

    /// <summary>
    /// Returns a new handle to the same pixel buffer. The caller must dispose it.
    /// </summary>
    public Frame Share()
    {
        FrameBuffer buffer = GetBuffer();
        buffer.AddRef();
        return new Frame(buffer, this);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _buffer, null)?.Release();
    }

    public override string ToString() =>
        $"Frame #{FrameId} {Width}x{Height} {PixelFormat}{(IsDisposed ? " (disposed)" : string.Empty)}";

    private FrameBuffer GetBuffer() =>
        Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(Frame), $"Frame #{FrameId} has been disposed.");
}
