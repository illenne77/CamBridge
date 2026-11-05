namespace CamBridge.Sources.Pylon;

/// <summary>
/// One grab result as pylon delivered it, before any copy or conversion: what a transport diagnosis needs, without the
/// pixels. See <see cref="PylonOptions.GrabObserver"/>.
/// </summary>
/// <param name="Succeeded">False for an incomplete or failed grab; its frame is lost.</param>
/// <param name="FrameId">
/// The unwrapped block ID, the same value <see cref="Core.Frame.FrameId"/> would carry. Gaps between consecutive ids are
/// frames that produced no grab result at all (for example buffer underruns). Null for a failed grab whose block ID says
/// nothing (0): that frame then shows up as a gap before the next good one, so count it there, not twice.
/// </param>
/// <param name="ErrorCode">pylon's error code for a failed grab; 0 when it succeeded.</param>
/// <param name="ErrorDescription">pylon's description for a failed grab; null when it succeeded.</param>
/// <param name="DeviceTimestamp">The camera's timestamp for the frame, in camera ticks.</param>
/// <param name="HostTimestamp"><see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> when the result reached the grab thread.</param>
/// <param name="PayloadSize">Bytes the camera sent for the frame.</param>
/// <param name="PixelFormat">pylon's name for the frame's pixel type (e.g. "Mono8", "Mono12"); empty when the result has no image.</param>
public readonly record struct PylonGrab(
    bool Succeeded,
    long? FrameId,
    int ErrorCode,
    string? ErrorDescription,
    long DeviceTimestamp,
    long HostTimestamp,
    long PayloadSize,
    string PixelFormat);
