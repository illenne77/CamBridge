namespace CamBridge.Inspection;

public sealed record InspectionResult(
    long FrameId,
    DateTime TimestampUtc,
    Verdict Verdict,
    IReadOnlyList<ToolResult> Tools,
    TimeSpan Duration)
{
    /// <summary>All failure reasons across tools, in tool order.</summary>
    public IEnumerable<string> Reasons => Tools.SelectMany(t => t.Reasons);

    public IEnumerable<OverlayRect> Overlays => Tools.SelectMany(t => t.Overlays);
}
