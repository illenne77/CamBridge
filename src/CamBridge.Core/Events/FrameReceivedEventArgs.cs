namespace CamBridge.Core;

/// <summary>
/// Carries a frame from the acquisition thread. <see cref="Frame"/> is owned by the camera
/// and is disposed after all handlers return; call <see cref="Frame.Share"/> to keep it.
/// </summary>
public sealed class FrameReceivedEventArgs(Frame frame) : EventArgs
{
    public Frame Frame { get; } = frame;
}
