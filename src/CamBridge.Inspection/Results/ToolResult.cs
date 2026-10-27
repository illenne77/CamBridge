namespace CamBridge.Inspection;

/// <param name="Reasons">Human-readable failure reasons, prefixed with the tool name. Empty when passed.</param>
/// <param name="IsConfigurationError">True when the tool could not run as configured (e.g. ROI outside the image).</param>
public sealed record ToolResult(
    string ToolName,
    bool Passed,
    IReadOnlyList<Measurement> Measurements,
    IReadOnlyList<string> Reasons,
    IReadOnlyList<OverlayRect> Overlays,
    TimeSpan Duration,
    bool IsConfigurationError = false)
{
    internal static ToolResult FromMeasurements(
        string toolName, IReadOnlyList<Measurement> measurements, IReadOnlyList<OverlayRect> overlays, TimeSpan duration)
    {
        string[] reasons = measurements
            .Select(m => m.Limit?.DescribeViolation(m.Name, m.Value))
            .OfType<string>()
            .Select(r => $"{toolName}: {r}")
            .ToArray();
        return new ToolResult(toolName, reasons.Length == 0, measurements, reasons, overlays, duration);
    }

    internal static ToolResult ConfigurationError(string toolName, string message, IReadOnlyList<OverlayRect> overlays, TimeSpan duration) =>
        new(toolName, false, [], [$"{toolName}: configuration error: {message}"], overlays, duration, IsConfigurationError: true);
}
