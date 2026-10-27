using System.Buffers;

namespace CamBridge.Core;

/// <summary>
/// Reference-counted pooled byte array shared by one or more <see cref="Frame"/> handles.
/// The array goes back to its pool when the last reference is released.
/// </summary>
internal sealed class FrameBuffer
{
    private readonly ArrayPool<byte> _pool;
    private byte[]? _array;
    private int _refCount = 1;

    public FrameBuffer(ArrayPool<byte> pool, int length)
    {
        _pool = pool;
        _array = pool.Rent(length);
    }

    public int RefCount => Volatile.Read(ref _refCount);

    public byte[] Array => _array ?? throw new ObjectDisposedException(nameof(Frame));

    /// <summary>Adds a reference. Only valid while the caller already holds one.</summary>
    public void AddRef()
    {
        int current;
        do
        {
            current = Volatile.Read(ref _refCount);
            ObjectDisposedException.ThrowIf(current <= 0, typeof(Frame));
        }
        while (Interlocked.CompareExchange(ref _refCount, current + 1, current) != current);
    }

    public void Release()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining == 0)
        {
            byte[]? array = Interlocked.Exchange(ref _array, null);
            if (array is not null)
            {
                _pool.Return(array);
            }
        }
        else if (remaining < 0)
        {
            throw new InvalidOperationException("Frame buffer released more times than it was referenced.");
        }
    }
}
