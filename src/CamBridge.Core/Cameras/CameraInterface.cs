namespace CamBridge.Core;

/// <summary>Physical or virtual transport the camera is reached through.</summary>
public enum CameraInterface
{
    Unknown,
    GigE,
    Usb3,
    Emulated,
    Webcam,
    File,
}
