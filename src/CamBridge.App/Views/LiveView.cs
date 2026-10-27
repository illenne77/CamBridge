using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CamBridge.App.Imaging;
using CamBridge.Core;
using CamBridge.Inspection;
using CamBridge.Pipeline;
using Frame = CamBridge.Core.Frame;
using WpfPixelFormat = System.Windows.Media.PixelFormat;

namespace CamBridge.App.Views;

/// <summary>
/// Displays the latest frame of a <see cref="LatestFrameSlot"/>. Frames are pulled once per
/// WPF render pass (CompositionTarget.Rendering), so the camera never waits for the UI and a
/// slow UI simply skips frames. Supports wheel zoom at the cursor, drag to pan, double-click
/// to fit, and shows the pixel value under the cursor.
/// </summary>
public sealed class LiveView : Control
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(
        nameof(Source), typeof(LatestFrameSlot), typeof(LiveView), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty DisplayFpsProperty = DependencyProperty.Register(
        nameof(DisplayFps), typeof(double), typeof(LiveView),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty OverlayProperty = DependencyProperty.Register(
        nameof(Overlay), typeof(IInspectionOverlay), typeof(LiveView), new PropertyMetadata(null, OnOverlaySettingChanged));

    public static readonly DependencyProperty ShowOverlayProperty = DependencyProperty.Register(
        nameof(ShowOverlay), typeof(bool), typeof(LiveView),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnOverlaySettingChanged));

    public static readonly DependencyProperty IsRoiEditingProperty = DependencyProperty.Register(
        nameof(IsRoiEditing), typeof(bool), typeof(LiveView),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnOverlaySettingChanged));

    private const double WheelZoomFactor = 1.25;
    private const double HandleToleranceScreen = 6;
    private static readonly TimeSpan FpsWindow = TimeSpan.FromSeconds(1);

    private readonly ViewTransform _transform = new();
    private readonly Image _image = new() { Stretch = Stretch.None };
    private readonly MatrixTransform _imageTransform = new();
    private readonly TextBlock _info = new() { Foreground = Brushes.White, Margin = new Thickness(6, 4, 6, 4), FontFamily = new FontFamily("Consolas") };
    private readonly TextBlock _placeholder = new() { Foreground = Brushes.Gray, Text = "영상 없음", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid _root = new() { Background = Brushes.Black, ClipToBounds = true };
    private readonly Canvas _canvas = new();
    private readonly OverlayLayer _overlayLayer = new();

    private WriteableBitmap? _bitmap;
    private Point? _panStart;
    private long _overlayVersion = -1;
    private bool _overlayDirty = true;

    // ROI drag in progress (edit mode).
    private int _editIndex = -1;
    private RoiHandle _editHandle;
    private bool _editDrawing;
    private RoiRect _editStartRoi;
    private (double X, double Y) _editStartImage;
    private RoiRect _editCurrentRoi;
    private Point? _cursor;
    private long _lastFrameId;
    private int _framesInWindow;
    private long _windowStart = Stopwatch.GetTimestamp();

    static LiveView()
    {
        FocusableProperty.OverrideMetadata(typeof(LiveView), new FrameworkPropertyMetadata(true));
    }

    public LiveView()
    {
        _image.RenderTransform = _imageTransform;
        _canvas.Children.Add(_image);
        _root.Children.Add(_canvas);
        _root.Children.Add(_overlayLayer);
        _root.Children.Add(_placeholder);

        var infoBar = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = _info,
        };
        _root.Children.Add(infoBar);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(4) };
        toolbar.Children.Add(ToolbarToggle("오버레이", ShowOverlayProperty));
        toolbar.Children.Add(ToolbarToggle("ROI 편집", IsRoiEditingProperty));
        toolbar.Children.Add(ToolbarButton("Fit", (_, _) => FitToView()));
        toolbar.Children.Add(ToolbarButton("1:1", (_, _) => ActualSize()));
        _root.Children.Add(toolbar);

        AddVisualChild(_root);
        AddLogicalChild(_root);

        Loaded += (_, _) => CompositionTarget.Rendering += OnRendering;
        Unloaded += (_, _) => CompositionTarget.Rendering -= OnRendering;
        SizeChanged += (_, _) =>
        {
            if (_transform.IsFitted)
            {
                FitToView();
            }
        };
    }

    public LatestFrameSlot? Source
    {
        get => (LatestFrameSlot?)GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    /// <summary>Frames actually drawn per second (bind OneWayToSource to show it).</summary>
    public double DisplayFps
    {
        get => (double)GetValue(DisplayFpsProperty);
        set => SetValue(DisplayFpsProperty, value);
    }

    /// <summary>Inspection shapes to draw and ROIs to edit; pulled on the render tick like frames.</summary>
    public IInspectionOverlay? Overlay
    {
        get => (IInspectionOverlay?)GetValue(OverlayProperty);
        set => SetValue(OverlayProperty, value);
    }

    public bool ShowOverlay
    {
        get => (bool)GetValue(ShowOverlayProperty);
        set => SetValue(ShowOverlayProperty, value);
    }

    /// <summary>When true the left button edits ROIs (select, move, resize, draw) instead of panning.</summary>
    public bool IsRoiEditing
    {
        get => (bool)GetValue(IsRoiEditingProperty);
        set => SetValue(IsRoiEditingProperty, value);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => index == 0 ? _root : throw new ArgumentOutOfRangeException(nameof(index));

    protected override Size MeasureOverride(Size constraint)
    {
        _root.Measure(constraint);
        return new Size(0, 0); // take whatever the layout gives; the image is scaled, not measured
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        _root.Arrange(new Rect(arrangeBounds));
        return arrangeBounds;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        if (_bitmap is null)
        {
            return;
        }

        Point p = e.GetPosition(this);
        _transform.ZoomAt(e.Delta > 0 ? WheelZoomFactor : 1 / WheelZoomFactor, p.X, p.Y);
        ApplyTransform();
        e.Handled = true;
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseDown(e);
        Focus();
        if (e.ChangedButton == MouseButton.Left && e.ClickCount == 2)
        {
            FitToView();
            e.Handled = true;
            return;
        }

        if (e.ChangedButton == MouseButton.Left && IsRoiEditing)
        {
            if (BeginRoiEdit(e.GetPosition(this)))
            {
                CaptureMouse();
            }

            e.Handled = true;
            return;
        }

        // Left drag pans unless ROI editing; middle drag always pans.
        if (e.ChangedButton is MouseButton.Left or MouseButton.Middle)
        {
            _panStart = e.GetPosition(this);
            CaptureMouse();
            Cursor = Cursors.SizeAll;
            e.Handled = true;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseMove(e);
        Point p = e.GetPosition(this);
        if (_panStart is Point start)
        {
            _transform.Pan(p.X - start.X, p.Y - start.Y);
            _panStart = p;
            ApplyTransform();
        }
        else if (_editIndex >= 0)
        {
            ContinueRoiEdit(p);
        }
        else if (IsRoiEditing)
        {
            Cursor = HoverCursor(p);
        }

        _cursor = p;
        UpdateInfo();
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseUp(e);
        if (_editIndex >= 0 && e.ChangedButton == MouseButton.Left)
        {
            Overlay?.CommitRoi(_editIndex, _editCurrentRoi);
            _editIndex = -1;
            ReleaseMouseCapture();
            return;
        }

        if (_panStart is not null)
        {
            _panStart = null;
            ReleaseMouseCapture();
            Cursor = null;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _cursor = null;
        UpdateInfo();
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LiveView)d;
        if (e.NewValue is null)
        {
            view.Clear();
        }
    }

    private static void OnOverlaySettingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (LiveView)d;
        view._overlayDirty = true;
        if (!view.IsRoiEditing)
        {
            view.Cursor = null;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        UpdateFps();

        Frame? frame = Source?.TryTake();
        if (frame is not null)
        {
            using (frame)
            {
                Draw(frame);
            }
        }

        UpdateOverlay();
    }

    /// <summary>Redraws the overlay only when its content or the view transform changed.</summary>
    private void UpdateOverlay()
    {
        IInspectionOverlay? overlay = ShowOverlay && _bitmap is not null ? Overlay : null;
        if (overlay is null)
        {
            if (_overlayDirty)
            {
                _overlayLayer.Show(OverlaySnapshot.Empty, _transform, showHandles: false);
                _overlayDirty = false;
            }

            return;
        }

        long version = overlay.Version;
        if (version != _overlayVersion || _overlayDirty)
        {
            _overlayVersion = version;
            _overlayDirty = false;
            _overlayLayer.Show(overlay.GetSnapshot(), _transform, IsRoiEditing);
        }
    }

    private bool BeginRoiEdit(Point screen)
    {
        if (Overlay is not { } overlay || _bitmap is null)
        {
            return false;
        }

        (double x, double y) = _transform.ToImage(screen.X, screen.Y);
        (int index, RoiHandle handle) = HitTest(overlay, x, y);
        if (index >= 0)
        {
            overlay.SelectRoi(index);
            _editDrawing = false;
            _editHandle = handle;
        }
        else if (overlay.SelectedIndex >= 0)
        {
            // Empty area: draw a new ROI for the selected tool.
            index = overlay.SelectedIndex;
            _editDrawing = true;
        }
        else
        {
            return false;
        }

        _editIndex = index;
        _editStartRoi = overlay.EditableRois[index];
        _editStartImage = (x, y);
        _editCurrentRoi = _editStartRoi;
        return true;
    }

    private void ContinueRoiEdit(Point screen)
    {
        if (Overlay is not { } overlay || _bitmap is null)
        {
            return;
        }

        (double x, double y) = _transform.ToImage(screen.X, screen.Y);
        _editCurrentRoi = _editDrawing
            ? RoiEditor.FromCorners(_editStartImage.X, _editStartImage.Y, x, y, _bitmap.PixelWidth, _bitmap.PixelHeight)
            : RoiEditor.Drag(_editStartRoi, _editHandle, x - _editStartImage.X, y - _editStartImage.Y, _bitmap.PixelWidth, _bitmap.PixelHeight);
        overlay.PreviewRoi(_editIndex, _editCurrentRoi);
    }

    /// <summary>The selected ROI wins ties so its handles stay grabbable where ROIs overlap.</summary>
    private (int Index, RoiHandle Handle) HitTest(IInspectionOverlay overlay, double x, double y)
    {
        double tolerance = HandleToleranceScreen / _transform.Scale;
        IReadOnlyList<RoiRect> rois = overlay.EditableRois;
        int selected = overlay.SelectedIndex;
        if (selected >= 0 && selected < rois.Count && RoiEditor.HitTest(rois[selected], x, y, tolerance) is var h && h != RoiHandle.None)
        {
            return (selected, h);
        }

        for (int i = rois.Count - 1; i >= 0; i--)
        {
            RoiHandle handle = RoiEditor.HitTest(rois[i], x, y, tolerance);
            if (handle != RoiHandle.None)
            {
                return (i, handle);
            }
        }

        return (-1, RoiHandle.None);
    }

    private Cursor? HoverCursor(Point screen)
    {
        if (Overlay is not { } overlay || _bitmap is null)
        {
            return null;
        }

        (double x, double y) = _transform.ToImage(screen.X, screen.Y);
        return HitTest(overlay, x, y).Handle switch
        {
            RoiHandle.Body => Cursors.SizeAll,
            RoiHandle.Left or RoiHandle.Right => Cursors.SizeWE,
            RoiHandle.Top or RoiHandle.Bottom => Cursors.SizeNS,
            RoiHandle.TopLeft or RoiHandle.BottomRight => Cursors.SizeNWSE,
            RoiHandle.TopRight or RoiHandle.BottomLeft => Cursors.SizeNESW,
            _ => overlay.SelectedIndex >= 0 ? Cursors.Cross : null,
        };
    }

    private void Draw(Frame frame)
    {
        WpfPixelFormat format = frame.PixelFormat switch
        {
            Core.PixelFormat.Mono8 => PixelFormats.Gray8,
            Core.PixelFormat.Bgr8 => PixelFormats.Bgr24,
            _ => throw new NotSupportedException($"Pixel format {frame.PixelFormat} cannot be displayed."),
        };

        bool sizeChanged = _bitmap is null || _bitmap.PixelWidth != frame.Width || _bitmap.PixelHeight != frame.Height || _bitmap.Format != format;
        if (sizeChanged)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, format, null);
            _image.Source = _bitmap;
            _placeholder.Visibility = Visibility.Collapsed;
        }

        using (MemoryHandle pin = frame.Memory.Pin())
        {
            unsafe
            {
                _bitmap!.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), (IntPtr)pin.Pointer, frame.Length, frame.Stride);
            }
        }

        _lastFrameId = frame.FrameId;
        _framesInWindow++;
        if (sizeChanged)
        {
            FitToView();
        }

        UpdateInfo();
    }

    private void UpdateFps()
    {
        TimeSpan elapsed = Stopwatch.GetElapsedTime(_windowStart);
        if (elapsed >= FpsWindow)
        {
            DisplayFps = _framesInWindow / elapsed.TotalSeconds;
            _framesInWindow = 0;
            _windowStart = Stopwatch.GetTimestamp();
        }
    }

    private void FitToView()
    {
        if (_bitmap is not null)
        {
            _transform.Fit(ActualWidth, ActualHeight, _bitmap.PixelWidth, _bitmap.PixelHeight);
            ApplyTransform();
        }
    }

    private void ActualSize()
    {
        if (_bitmap is not null)
        {
            _transform.ActualSize(ActualWidth, ActualHeight, _bitmap.PixelWidth, _bitmap.PixelHeight);
            ApplyTransform();
        }
    }

    private void ApplyTransform()
    {
        _imageTransform.Matrix = new Matrix(_transform.Scale, 0, 0, _transform.Scale, _transform.OffsetX, _transform.OffsetY);
        _overlayDirty = true;
        // Show individual pixels when magnified; smooth when shrunk.
        RenderOptions.SetBitmapScalingMode(_image, _transform.Scale >= 1 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.Linear);
        UpdateInfo();
    }

    private void UpdateInfo()
    {
        if (_bitmap is null)
        {
            _info.Text = string.Empty;
            return;
        }

        string text = string.Create(CultureInfo.InvariantCulture,
            $"#{_lastFrameId}  {_bitmap.PixelWidth}x{_bitmap.PixelHeight}  {_transform.Scale * 100:0}%");
        if (_cursor is Point c && _transform.PixelAt(c.X, c.Y, _bitmap.PixelWidth, _bitmap.PixelHeight) is (int x, int y))
        {
            text += string.Create(CultureInfo.InvariantCulture, $"  ({x}, {y}) = {ReadPixel(x, y)}");
        }

        _info.Text = text;
    }

    private string ReadPixel(int x, int y)
    {
        var pixel = new byte[3];
        int bytesPerPixel = (_bitmap!.Format.BitsPerPixel + 7) / 8;
        _bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, bytesPerPixel, 0);
        return bytesPerPixel == 1
            ? pixel[0].ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"R{pixel[2]} G{pixel[1]} B{pixel[0]}");
    }

    private void Clear()
    {
        _bitmap = null;
        _image.Source = null;
        _placeholder.Visibility = Visibility.Visible;
        _info.Text = string.Empty;
    }

    private ToggleButton ToolbarToggle(string text, DependencyProperty property)
    {
        var toggle = new ToggleButton { Content = text, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1), Focusable = false };
        toggle.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property.Name) { Source = this, Mode = BindingMode.TwoWay });
        return toggle;
    }

    private static Button ToolbarButton(string text, RoutedEventHandler onClick)
    {
        var button = new Button { Content = text, MinWidth = 36, Margin = new Thickness(2, 0, 0, 0), Padding = new Thickness(6, 1, 6, 1), Focusable = false };
        button.Click += onClick;
        return button;
    }
}
