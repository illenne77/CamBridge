namespace CamBridge.Core;

public enum TriggerMode
{
    /// <summary>Free-run: the camera captures continuously at its frame rate.</summary>
    Off,

    /// <summary>One frame per <see cref="ICamera.ExecuteSoftwareTrigger"/> call.</summary>
    Software,

    /// <summary>One frame per external trigger signal (line source is a generic parameter).</summary>
    Hardware,
}
