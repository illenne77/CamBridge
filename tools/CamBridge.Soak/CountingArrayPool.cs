using System.Buffers;

namespace CamBridge.Soak;

/// <summary>
/// Real pooling (delegates to <see cref="ArrayPool{T}.Shared"/>) plus rent/return counters, so
/// the soak run can prove that frame buffers are returned: outstanding buffers must stay bounded
/// by the queues and the live view slot, not grow with run time.
/// </summary>
internal sealed class CountingArrayPool : ArrayPool<byte>
{
    private long _rented;
    private long _returned;

    public long Rented => Interlocked.Read(ref _rented);

    public long Outstanding => Rented - Interlocked.Read(ref _returned);

    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rented);
        return Shared.Rent(minimumLength);
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Interlocked.Increment(ref _returned);
        Shared.Return(array, clearArray);
    }
}
