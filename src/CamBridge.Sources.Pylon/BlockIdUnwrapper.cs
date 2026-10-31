namespace CamBridge.Sources.Pylon;

/// <summary>
/// Turns the camera's block ID into a monotonic frame id, so that gaps (frames lost in
/// transport) show up as gaps in <see cref="Core.Frame.FrameId"/> and are counted as camera-side
/// drops by the pipeline.
/// </summary>
/// <remarks>
/// GigE Vision block IDs run 1..65535 and wrap to 1 (0 means "not supported"); USB3 Vision and
/// the emulator count from 0 over the full 64-bit range. A wrap is recognized when the raw value
/// falls back by more than half the 16-bit range. When the camera reports no block ID, a host
/// counter is used instead (no camera-side drop detection).
/// </remarks>
internal sealed class BlockIdUnwrapper(bool sixteenBitWrap)
{
    private const long GigEPeriod = 65535; // IDs 1..65535

    private long _offset;
    private long _lastRaw = -1;
    private long _hostCounter;

    public long Next(long rawBlockId)
    {
        if (sixteenBitWrap && rawBlockId == 0)
        {
            return ++_hostCounter; // GigE camera without block ID support
        }

        if (sixteenBitWrap && _lastRaw >= 0 && rawBlockId < _lastRaw && _lastRaw - rawBlockId > GigEPeriod / 2)
        {
            _offset += GigEPeriod;
        }

        _lastRaw = rawBlockId;
        return _offset + rawBlockId;
    }
}
