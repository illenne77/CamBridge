using CamBridge.Core;

namespace CamBridge.Pipeline.Saving;

/// <summary>Encodes a frame into an image file payload.</summary>
public interface IFrameEncoder
{
    /// <param name="jpegQuality">0-100; used only for <see cref="ImageFileFormat.Jpeg"/>.</param>
    byte[] Encode(Frame frame, ImageFileFormat format, int jpegQuality);
}
