using System.Buffers;
using CamBridge.Core;
using OpenCvSharp;

namespace CamBridge.Inspection;

/// <summary>Bridges <see cref="Frame"/> buffers to OpenCV without copying where possible.</summary>
public static class FrameMat
{
    /// <summary>
    /// Runs <paramref name="action"/> with an 8-bit grayscale view of the frame. Mono8 frames are
    /// wrapped in place (pinned for the duration of the call); Bgr8 frames are converted once.
    /// The Mat must not be used after the call returns.
    /// </summary>
    public static unsafe T WithGray<T>(Frame frame, Func<Mat, T> action)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(action);

        using MemoryHandle pin = frame.Memory.Pin();
        var data = (IntPtr)pin.Pointer;
        switch (frame.PixelFormat)
        {
            case Core.PixelFormat.Mono8:
                using (Mat mono = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC1, data, frame.Stride))
                {
                    return action(mono);
                }

            case Core.PixelFormat.Bgr8:
                using (Mat bgr = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3, data, frame.Stride))
                using (var gray = new Mat())
                {
                    Cv2.CvtColor(bgr, gray, ColorConversionCodes.BGR2GRAY);
                    return action(gray);
                }

            default:
                throw new NotSupportedException($"Pixel format {frame.PixelFormat} is not supported for inspection.");
        }
    }

    /// <summary>Copies an 8-bit Mat (1 or 3 channels) into a new pooled frame.</summary>
    public static Frame ToFrame(Mat image, long frameId, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(image);

        Core.PixelFormat format = image.Type() == MatType.CV_8UC1 ? Core.PixelFormat.Mono8
            : image.Type() == MatType.CV_8UC3 ? Core.PixelFormat.Bgr8
            : throw new NotSupportedException($"Mat type {image.Type()} cannot be converted to a frame.");

        Frame frame = Frame.Rent(image.Width, image.Height, format, frameId, timestampUtc);
        Span<byte> target = frame.GetWritableData();
        int rowBytes = frame.Stride;
        for (int y = 0; y < image.Height; y++)
        {
            unsafe
            {
                new ReadOnlySpan<byte>((void*)image.Ptr(y), rowBytes).CopyTo(target.Slice(y * rowBytes, rowBytes));
            }
        }

        return frame;
    }
}
