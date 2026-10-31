using CamBridge.Inspection;

namespace CamBridge.App.Imaging;

/// <summary>Part of a ROI under the cursor, or what a drag changes.</summary>
[Flags]
public enum RoiHandle
{
    None = 0,
    Left = 1,
    Top = 2,
    Right = 4,
    Bottom = 8,
    TopLeft = Top | Left,
    TopRight = Top | Right,
    BottomLeft = Bottom | Left,
    BottomRight = Bottom | Right,

    /// <summary>Inside the rectangle: the drag moves it.</summary>
    Body = 16,
}

/// <summary>
/// ROI hit testing and drag geometry in image pixel coordinates. Results always lie inside the
/// image and keep a minimum size, so an edited ROI can never make a tool fail with a
/// configuration error. Free of WPF types for unit testing.
/// </summary>
public static class RoiEditor
{
    public const int MinSize = 4;

    /// <summary>Finds the handle at an image point. <paramref name="tolerance"/> is in image pixels (screen tolerance / zoom).</summary>
    public static RoiHandle HitTest(RoiRect roi, double x, double y, double tolerance)
    {
        bool withinX = x >= roi.X - tolerance && x <= roi.Right + tolerance;
        bool withinY = y >= roi.Y - tolerance && y <= roi.Bottom + tolerance;
        if (!withinX || !withinY)
        {
            return RoiHandle.None;
        }

        RoiHandle handle = RoiHandle.None;
        if (Math.Abs(x - roi.X) <= tolerance)
        {
            handle |= RoiHandle.Left;
        }
        else if (Math.Abs(x - roi.Right) <= tolerance)
        {
            handle |= RoiHandle.Right;
        }

        if (Math.Abs(y - roi.Y) <= tolerance)
        {
            handle |= RoiHandle.Top;
        }
        else if (Math.Abs(y - roi.Bottom) <= tolerance)
        {
            handle |= RoiHandle.Bottom;
        }

        if (handle != RoiHandle.None)
        {
            return handle;
        }

        return x > roi.X && x < roi.Right && y > roi.Y && y < roi.Bottom ? RoiHandle.Body : RoiHandle.None;
    }

    /// <summary>Applies a drag of (<paramref name="dx"/>, <paramref name="dy"/>) image pixels to <paramref name="start"/>.</summary>
    public static RoiRect Drag(RoiRect start, RoiHandle handle, double dx, double dy, int imageWidth, int imageHeight)
    {
        if (handle == RoiHandle.Body)
        {
            int width = Math.Min(start.Width, imageWidth);
            int height = Math.Min(start.Height, imageHeight);
            int x = Math.Clamp(start.X + (int)Math.Round(dx), 0, imageWidth - width);
            int y = Math.Clamp(start.Y + (int)Math.Round(dy), 0, imageHeight - height);
            return new RoiRect(x, y, width, height);
        }

        int left = start.X;
        int top = start.Y;
        int right = start.Right;
        int bottom = start.Bottom;
        int ix = (int)Math.Round(dx);
        int iy = (int)Math.Round(dy);

        if (handle.HasFlag(RoiHandle.Left))
        {
            left = Math.Clamp(left + ix, 0, right - MinSize);
        }
        else if (handle.HasFlag(RoiHandle.Right))
        {
            right = Math.Clamp(right + ix, left + MinSize, imageWidth);
        }

        if (handle.HasFlag(RoiHandle.Top))
        {
            top = Math.Clamp(top + iy, 0, bottom - MinSize);
        }
        else if (handle.HasFlag(RoiHandle.Bottom))
        {
            bottom = Math.Clamp(bottom + iy, top + MinSize, imageHeight);
        }

        return Clamp(new RoiRect(left, top, right - left, bottom - top), imageWidth, imageHeight);
    }

    /// <summary>Rectangle spanned by two image points (any order), covering every pixel touched.</summary>
    public static RoiRect FromCorners(double x0, double y0, double x1, double y1, int imageWidth, int imageHeight)
    {
        int left = (int)Math.Floor(Math.Min(x0, x1));
        int top = (int)Math.Floor(Math.Min(y0, y1));
        int right = (int)Math.Ceiling(Math.Max(x0, x1));
        int bottom = (int)Math.Ceiling(Math.Max(y0, y1));
        return Clamp(new RoiRect(left, top, Math.Max(right - left, MinSize), Math.Max(bottom - top, MinSize)), imageWidth, imageHeight);
    }

    /// <summary>Moves and shrinks <paramref name="roi"/> as needed to fit the image, keeping <see cref="MinSize"/> where possible.</summary>
    public static RoiRect Clamp(RoiRect roi, int imageWidth, int imageHeight)
    {
        int width = Math.Clamp(roi.Width, Math.Min(MinSize, imageWidth), imageWidth);
        int height = Math.Clamp(roi.Height, Math.Min(MinSize, imageHeight), imageHeight);
        int x = Math.Clamp(roi.X, 0, imageWidth - width);
        int y = Math.Clamp(roi.Y, 0, imageHeight - height);
        return new RoiRect(x, y, width, height);
    }
}
