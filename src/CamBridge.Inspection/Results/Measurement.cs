namespace CamBridge.Inspection;

/// <summary>A measured value and the range it was judged against (null = informational only).</summary>
public sealed record Measurement(string Name, double Value, ValueRange? Limit = null)
{
    public bool Passed => Limit?.Contains(Value) ?? true;
}
