using System.Buffers;
using CamBridge.Core;
using OpenCvSharp;

namespace CamBridge.Pipeline.Saving;

/// <summary>Encodes with OpenCV, wrapping the pinned frame buffer as a Mat without copying.</summary>
public sealed class OpenCvFrameEncoder : IFrameEncoder
{
    public unsafe byte[] Encode(Frame frame, ImageFileFormat format, int jpegQuality)
    {
        ArgumentNullException.ThrowIfNull(frame);

        MatType type = frame.PixelFormat switch
        {
            Core.PixelFormat.Mono8 => MatType.CV_8UC1,
            Core.PixelFormat.Bgr8 => MatType.CV_8UC3,
            _ => throw new NotSupportedException($"Pixel format {frame.PixelFormat} cannot be encoded."),
        };

        int[] parameters = format == ImageFileFormat.Jpeg
            ? [(int)ImwriteFlags.JpegQuality, Math.Clamp(jpegQuality, 0, 100)]
            : [];

        using MemoryHandle pin = frame.Memory.Pin();
        using Mat mat = Mat.FromPixelData(frame.Height, frame.Width, type, (IntPtr)pin.Pointer, frame.Stride);
        if (!Cv2.ImEncode(format.FileExtension(), mat, out byte[] encoded, parameters))
        {
            throw new InvalidOperationException($"OpenCV failed to encode frame #{frame.FrameId} as {format}.");
        }

        return encoded;
    }
}
