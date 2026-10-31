using System.Text.Json.Serialization;

namespace CamBridge.Inspection;

/// <summary>Configuration of one inspection tool. Serialized polymorphically with a "$type" discriminator.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(BlobToolSettings), "blob")]
[JsonDerivedType(typeof(TemplateMatchToolSettings), "templateMatch")]
public abstract record ToolSettings
{
    /// <summary>Unique within a recipe; used in reasons and CSV column names.</summary>
    public required string Name { get; init; }

    public required RoiRect Roi { get; init; }

    public bool Enabled { get; init; } = true;
}

public enum ThresholdMode
{
    /// <summary>Use <see cref="BlobToolSettings.Threshold"/>.</summary>
    Fixed,

    /// <summary>Compute the threshold per image with Otsu's method.</summary>
    Otsu,
}

public enum BlobPolarity
{
    /// <summary>Objects are darker than the background (e.g. stains, holes on a bright part).</summary>
    Dark,

    /// <summary>Objects are brighter than the background.</summary>
    Bright,
}

/// <summary>Binarizes the ROI and measures connected regions (count and area).</summary>
public sealed record BlobToolSettings : ToolSettings
{
    public ThresholdMode ThresholdMode { get; init; } = ThresholdMode.Fixed;

    /// <summary>0-255 gray level; ignored for <see cref="ThresholdMode.Otsu"/>.</summary>
    public int Threshold { get; init; } = 128;

    public BlobPolarity Polarity { get; init; } = BlobPolarity.Dark;

    /// <summary>Blobs smaller than this (in pixels) are treated as noise and ignored.</summary>
    public int MinBlobArea { get; init; } = 1;

    public ValueRange? Count { get; init; }

    public ValueRange? TotalArea { get; init; }

    public ValueRange? MaxArea { get; init; }
}

/// <summary>Searches the ROI for a reference pattern using normalized cross-correlation.</summary>
public sealed record TemplateMatchToolSettings : ToolSettings
{
    /// <summary>Template image path, relative to the recipe file's directory.</summary>
    public required string TemplatePath { get; init; }

    /// <summary>Minimum correlation score (0-1) for a pass.</summary>
    public double MinScore { get; init; } = 0.8;
}
