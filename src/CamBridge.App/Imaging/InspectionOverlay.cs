using CamBridge.Inspection;

namespace CamBridge.App.Imaging;

public enum OverlayStyle
{
    /// <summary>ROI without a result for its tool (e.g. not inspected yet).</summary>
    Roi,
    ToolPassed,
    ToolFailed,
    Blob,
    Match,
}

/// <summary>A rectangle in image coordinates. <paramref name="IsSelected"/> marks the ROI being edited.</summary>
public sealed record OverlayShape(RoiRect Rect, OverlayStyle Style, string? Label = null, bool IsSelected = false);

/// <param name="Verdict">Verdict banner, or null when nothing was inspected yet.</param>
public sealed record OverlaySnapshot(IReadOnlyList<OverlayShape> Shapes, Verdict? Verdict, long FrameId)
{
    public static OverlaySnapshot Empty { get; } = new([], null, 0);
}

/// <summary>
/// What the live view draws over the image and edits with the mouse. Implemented by the
/// inspection view model; the live view only knows this interface. All members are called on
/// the UI thread, from the render tick (pull, like the image itself).
/// </summary>
public interface IInspectionOverlay
{
    /// <summary>Changes whenever <see cref="GetSnapshot"/> would return something different.</summary>
    long Version { get; }

    OverlaySnapshot GetSnapshot();

    /// <summary>Tool ROIs in tool order; indexes are used by the editing members.</summary>
    IReadOnlyList<RoiRect> EditableRois { get; }

    int SelectedIndex { get; }

    void SelectRoi(int index);

    /// <summary>Shows <paramref name="roi"/> while dragging, without rebuilding the inspection.</summary>
    void PreviewRoi(int index, RoiRect roi);

    /// <summary>Applies the final ROI of a drag; the inspection is rebuilt with it.</summary>
    void CommitRoi(int index, RoiRect roi);
}
