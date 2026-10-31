using System.Globalization;
using System.Windows;
using System.Windows.Media;
using CamBridge.App.Imaging;
using CamBridge.Inspection;

namespace CamBridge.App.Views;

/// <summary>
/// Draws inspection shapes in screen coordinates (mapped through <see cref="ViewTransform"/>),
/// so lines stay one device-independent pixel wide at any zoom.
/// </summary>
internal sealed class OverlayLayer : FrameworkElement
{
    private const double HandleSize = 7;

    private static readonly Brush OkBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x40)));
    private static readonly Brush NgBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x41, 0x36)));
    private static readonly Brush RoiBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x7F, 0xDB, 0xFF)));
    private static readonly Brush BlobBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x85, 0x1B)));
    private static readonly Brush MatchBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF0, 0x12, 0xBE)));
    private static readonly Brush LabelBackground = Frozen(new SolidColorBrush(Color.FromArgb(170, 0, 0, 0)));
    private static readonly Typeface LabelFace = new("Segoe UI");

    private OverlaySnapshot _snapshot = OverlaySnapshot.Empty;
    private ViewTransform? _transform;
    private bool _showHandles;

    public OverlayLayer()
    {
        IsHitTestVisible = false;
    }

    public void Show(OverlaySnapshot snapshot, ViewTransform transform, bool showHandles)
    {
        _snapshot = snapshot;
        _transform = transform;
        _showHandles = showHandles;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        ArgumentNullException.ThrowIfNull(drawingContext);
        if (_transform is null)
        {
            return;
        }

        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        foreach (OverlayShape shape in _snapshot.Shapes)
        {
            Brush brush = shape.Style switch
            {
                OverlayStyle.ToolPassed => OkBrush,
                OverlayStyle.ToolFailed => NgBrush,
                OverlayStyle.Blob => BlobBrush,
                OverlayStyle.Match => MatchBrush,
                _ => RoiBrush,
            };
            Rect rect = ToScreen(shape.Rect);
            var pen = new Pen(brush, shape.IsSelected ? 2 : 1);
            if (shape.Style is OverlayStyle.Blob or OverlayStyle.Match)
            {
                pen.DashStyle = DashStyles.Dash;
            }

            drawingContext.DrawRectangle(null, pen, rect);
            if (shape.IsSelected && _showHandles)
            {
                DrawHandles(drawingContext, rect, brush);
            }

            if (shape.Label is not null)
            {
                DrawLabel(drawingContext, shape.Label, new Point(rect.Left, rect.Top), brush, 12, dpi, above: true);
            }
        }

        if (_snapshot.Verdict is Verdict verdict)
        {
            string text = string.Create(CultureInfo.InvariantCulture, $"{(verdict == Verdict.Ok ? "OK" : "NG")}  #{_snapshot.FrameId}");
            DrawLabel(drawingContext, text, new Point(8, 8), verdict == Verdict.Ok ? OkBrush : NgBrush, 22, dpi, above: false);
        }
    }

    private Rect ToScreen(RoiRect roi)
    {
        (double x0, double y0) = _transform!.ToScreen(roi.X, roi.Y);
        (double x1, double y1) = _transform.ToScreen(roi.Right, roi.Bottom);
        // Snap to device pixels so 1-px lines stay crisp.
        return new Rect(Math.Round(x0) + 0.5, Math.Round(y0) + 0.5, Math.Max(0, Math.Round(x1 - x0)), Math.Max(0, Math.Round(y1 - y0)));
    }

    private static void DrawHandles(DrawingContext dc, Rect rect, Brush brush)
    {
        double[] xs = [rect.Left, rect.Left + (rect.Width / 2), rect.Right];
        double[] ys = [rect.Top, rect.Top + (rect.Height / 2), rect.Bottom];
        foreach (double x in xs)
        {
            foreach (double y in ys)
            {
                if (x == xs[1] && y == ys[1])
                {
                    continue; // center: move by dragging the body
                }

                dc.DrawRectangle(Brushes.White, new Pen(brush, 1), new Rect(x - (HandleSize / 2), y - (HandleSize / 2), HandleSize, HandleSize));
            }
        }
    }

    private static void DrawLabel(DrawingContext dc, string text, Point origin, Brush brush, double size, double dpi, bool above)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, LabelFace, size, brush, dpi);
        double y = above ? origin.Y - formatted.Height - 1 : origin.Y;
        var box = new Rect(origin.X - 1, y, formatted.Width + 6, formatted.Height);
        dc.DrawRectangle(LabelBackground, null, box);
        dc.DrawText(formatted, new Point(origin.X + 2, y));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
