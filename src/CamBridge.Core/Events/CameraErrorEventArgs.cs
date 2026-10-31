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

public sealed class CameraErrorEventArgs(CameraErrorKind kind, string message, Exception? exception = null) : EventArgs
{
    public CameraErrorKind Kind { get; } = kind;

    public string Message { get; } = message;

    public Exception? Exception { get; } = exception;
}
