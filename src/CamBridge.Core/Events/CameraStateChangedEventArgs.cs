namespace CamBridge.Core;

public sealed class CameraStateChangedEventArgs(CameraState oldState, CameraState newState) : EventArgs
{
    public CameraState OldState { get; } = oldState;

    public CameraState NewState { get; } = newState;
}
