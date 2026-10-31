namespace CamBridge.Core;

/// <summary>
/// Describes one camera parameter (GenICam feature node). Values are read and written
/// through <see cref="ICamera.GetParameter"/> and <see cref="ICamera.SetParameter"/>.
/// </summary>
/// <remarks>
/// Strongly typed members of <see cref="ICamera"/> are described here as well, so a source
/// that cannot control e.g. gain reports it with <see cref="IsAvailable"/> = false.
/// </remarks>
public sealed record CameraParameter(string Name, ParameterType Type)
{
    /// <summary>False when the feature does not exist on this device or is locked by another setting.</summary>
    public bool IsAvailable { get; init; } = true;

    public bool IsReadable { get; init; } = true;

    public bool IsWritable { get; init; }

    /// <summary>Lower bound for Integer and Float parameters.</summary>
    public double? Minimum { get; init; }

    /// <summary>Upper bound for Integer and Float parameters.</summary>
    public double? Maximum { get; init; }

    /// <summary>Step size for Integer (and some Float) parameters.</summary>
    public double? Increment { get; init; }

    public string? Unit { get; init; }

    /// <summary>Allowed values for Enumeration parameters; empty otherwise.</summary>
    public IReadOnlyList<string> EnumEntries { get; init; } = [];

    public string? Description { get; init; }
}
