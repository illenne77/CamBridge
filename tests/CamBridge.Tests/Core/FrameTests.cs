using CamBridge.Core;
using CamBridge.Tests.Fakes;

namespace CamBridge.Tests.Core;

public class FrameTests
{
    private static Frame RentMono(TrackingArrayPool pool, int width = 4, int height = 3, int stride = 0) =>
        Frame.Rent(width, height, PixelFormat.Mono8, frameId: 7, DateTime.UtcNow, stride: stride, pool: pool);

    [Fact]
    public void Dispose_ReturnsBufferToPoolOnce()
    {
        var pool = new TrackingArrayPool();
        Frame frame = RentMono(pool);

        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, pool.Rented);
        Assert.Equal(1, pool.Returned);
        Assert.True(frame.IsDisposed);
    }

    [Fact]
    public void Share_KeepsBufferUntilLastHandleDisposed()
    {
        var pool = new TrackingArrayPool();
        Frame original = RentMono(pool);
        Frame shared = original.Share();

        original.Dispose();
        Assert.Equal(0, pool.Returned);
        Assert.Equal(12, shared.Data.Length);

        shared.Dispose();
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void Share_DoesNotCopyPixelData()
    {
        var pool = new TrackingArrayPool();
        using Frame original = RentMono(pool);
        original.GetWritableData()[0] = 42;

        using Frame shared = original.Share();

        Assert.Equal(1, pool.Rented);
        Assert.Equal(42, shared.Data[0]);
        Assert.Equal(original.FrameId, shared.FrameId);
    }

    [Fact]
    public void DoubleDisposeOfOneHandle_DoesNotReleaseOtherHolders()
    {
        var pool = new TrackingArrayPool();
        Frame original = RentMono(pool);
        Frame shared = original.Share();

        original.Dispose();
        original.Dispose();

        Assert.Equal(0, pool.Returned);
        Assert.False(shared.IsDisposed);
        shared.Dispose();
        Assert.Equal(1, pool.Returned);
    }

    [Fact]
    public void AccessAfterDispose_Throws()
    {
        Frame frame = RentMono(new TrackingArrayPool());
        frame.Dispose();

        Assert.Throws<ObjectDisposedException>(() => frame.Data.Length);
        Assert.Throws<ObjectDisposedException>(() => frame.Memory);
        Assert.Throws<ObjectDisposedException>(() => frame.Share());
        Assert.Throws<ObjectDisposedException>(() => frame.GetWritableData().Length);
    }

    [Fact]
    public void GetWritableData_ThrowsOnceShared()
    {
        using Frame frame = RentMono(new TrackingArrayPool());
        using Frame shared = frame.Share();

        Assert.Throws<InvalidOperationException>(() => frame.GetWritableData().Length);
    }

    [Fact]
    public void Rent_ComputesPackedStrideAndLength()
    {
        using Frame frame = Frame.Rent(10, 5, PixelFormat.Bgr8, 1, DateTime.UtcNow, pool: new TrackingArrayPool());

        Assert.Equal(30, frame.Stride);
        Assert.Equal(150, frame.Length);
        Assert.Equal(150, frame.Data.Length);
    }

    [Fact]
    public void Rent_HonorsPaddedStride()
    {
        using Frame frame = RentMono(new TrackingArrayPool(), width: 10, height: 2, stride: 16);

        Assert.Equal(32, frame.Data.Length);
    }

    [Theory]
    [InlineData(0, 1, 0)]
    [InlineData(1, 0, 0)]
    [InlineData(10, 1, 9)]
    public void Rent_RejectsInvalidGeometry(int width, int height, int stride)
    {
        var pool = new TrackingArrayPool();

        Assert.Throws<ArgumentOutOfRangeException>(() => RentMono(pool, width, height, stride));
        Assert.Equal(0, pool.Rented);
    }

    [Fact]
    public void ConcurrentShareAndDispose_ReturnsBufferExactlyOnce()
    {
        const int Iterations = 200;
        const int Holders = 16;
        var pool = new TrackingArrayPool();

        for (int i = 0; i < Iterations; i++)
        {
            Frame original = RentMono(pool);
            Frame[] handles = Enumerable.Range(0, Holders).Select(_ => original.Share()).ToArray();

            Parallel.ForEach(handles.Append(original), h => h.Dispose());
        }

        Assert.Equal(Iterations, pool.Rented);
        Assert.Equal(Iterations, pool.Returned);
    }
}
