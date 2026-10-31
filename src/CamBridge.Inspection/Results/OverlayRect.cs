namespace CamBridge.Inspection;

public enum OverlayKind
{
    /// <summary>The tool's region of interest.</summary>
    Roi,

    /// <summary>A detected blob's bounding box.</summary>
    Blob,

    /// <summary>Location of the best template match.</summary>
    Match,
}

/// <summary>Rectangle in image coordinates for drawing results over the live view.</summary>
public sealed record OverlayRect(RoiRect Rect, OverlayKind Kind);
