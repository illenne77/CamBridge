namespace CamBridge.Core;

public enum CameraState
{
    /// <summary>Not connected to the device.</summary>
    Closed,

    /// <summary>Connected and configurable, not acquiring.</summary>
    Open,

    /// <summary>Streaming frames.</summary>
    Acquiring,

    /// <summary>Unusable until closed and reopened (e.g. device removed).</summary>
    Faulted,
}
