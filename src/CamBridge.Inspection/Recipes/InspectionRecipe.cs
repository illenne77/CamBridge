namespace CamBridge.Inspection;

/// <summary>Complete inspection configuration. A frame is OK only when every enabled tool passes.</summary>
public sealed record InspectionRecipe
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public required string Name { get; init; }

    public IReadOnlyList<ToolSettings> Tools { get; init; } = [];
}
