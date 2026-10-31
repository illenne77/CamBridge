namespace CamBridge.Core;

public enum CameraErrorKind
{
    /// <summary>The device was removed or stopped responding; the camera enters <see cref="CameraState.Faulted"/>.</summary>
    Disconnected,

    /// <summary>A frame was incomplete or lost in transport; acquisition continues.</summary>
    FrameError,

    /// <summary>No frame arrived within the expected time.</summary>
    Timeout,

    Other,
}

public sealed class CameraErrorEventArgs(CameraErrorKind kind, string message, Exception? exception = null, long? lostFrameId = null) : EventArgs
{
    public CameraErrorKind Kind { get; } = kind;

    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;

    /// <summary>
    /// The <see cref="Frame.FrameId"/> of the frame this error lost, when the camera knows it. Lets
    /// the loss count right away instead of at the next good frame, which may never come.
    /// </summary>
    public long? LostFrameId { get; } = lostFrameId;
}
