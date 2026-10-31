using System.Buffers;

namespace CamBridge.Tests.Fakes;

/// <summary>Array pool that records rents and returns so tests can assert buffer ownership.</summary>
internal sealed class TrackingArrayPool : ArrayPool<byte>
{
    private int _rented;
    private int _returned;

    public int Rented => Volatile.Read(ref _rented);

    public int Returned => Volatile.Read(ref _returned);

    public int Outstanding => Rented - Returned;

    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rented);
        return new byte[minimumLength];
    }

    public override void Return(byte[] array, bool clearArray = false)
    {
        Interlocked.Increment(ref _returned);
    }
}
