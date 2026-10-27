using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CamBridge.Sources.Webcam.Interop;

/// <summary>Minimal DirectShow COM declarations (strmif.h) needed for device discovery and UVC controls.</summary>
internal static class DirectShowNative
{
    public const int S_OK = 0;

    public static readonly Guid SystemDeviceEnum = new("62BE5D10-60EB-11d0-BD3B-00A0C911CE86");
    public static readonly Guid VideoInputDeviceCategory = new("860BB310-5D01-11d0-BD3B-00A0C911CE86");
    public static readonly Guid IidBaseFilter = new("56A86895-0AD4-11CE-B03A-0020AF0BA770");
    public static readonly Guid IidPropertyBag = new("55272A00-42CB-11CE-8135-00AA004BB851");
}

/// <summary>CameraControlProperty (strmif.h).</summary>
internal enum CameraControlProperty
{
    Pan = 0,
    Tilt = 1,
    Roll = 2,
    Zoom = 3,
    Exposure = 4,
    Iris = 5,
    Focus = 6,
}

/// <summary>VideoProcAmpProperty (strmif.h).</summary>
internal enum VideoProcAmpProperty
{
    Brightness = 0,
    Contrast = 1,
    Hue = 2,
    Saturation = 3,
    Sharpness = 4,
    Gamma = 5,
    ColorEnable = 6,
    WhiteBalance = 7,
    BacklightCompensation = 8,
    Gain = 9,
}

/// <summary>CameraControl_Flags / VideoProcAmp_Flags; both use the same values.</summary>
[Flags]
internal enum ControlFlags
{
    None = 0,
    Auto = 1,
    Manual = 2,
}

[ComImport]
[Guid("29840822-5B84-11D0-BD3B-00A0C911CE86")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ICreateDevEnum
{
    /// <returns>S_OK, or S_FALSE (1) when the category is empty and <paramref name="enumMoniker"/> is null.</returns>
    [PreserveSig]
    int CreateClassEnumerator(ref Guid deviceClass, out IEnumMoniker? enumMoniker, int flags);
}

[ComImport]
[Guid("55272A00-42CB-11CE-8135-00AA004BB851")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyBag
{
    [PreserveSig]
    int Read([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] out object? value, IntPtr errorLog);

    [PreserveSig]
    int Write([MarshalAs(UnmanagedType.LPWStr)] string propertyName, [MarshalAs(UnmanagedType.Struct)] ref object value);
}

/// <summary>
/// Shared shape of IAMCameraControl and IAMVideoProcAmp. On failure the out values are left
/// untouched (often stale from a previous call), so callers must check the HRESULT.
/// </summary>
internal interface IUvcControlInterface
{
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out ControlFlags capsFlags);

    int Set(int property, int value, ControlFlags flags);

    int Get(int property, out int value, out ControlFlags flags);
}

[ComImport]
[Guid("C6E13370-30AC-11D0-A18C-00A0C9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMCameraControl
{
    [PreserveSig]
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out ControlFlags capsFlags);

    [PreserveSig]
    int Set(int property, int value, ControlFlags flags);

    [PreserveSig]
    int Get(int property, out int value, out ControlFlags flags);
}

[ComImport]
[Guid("C6E13360-30AC-11D0-A18C-00A0C9118956")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAMVideoProcAmp
{
    [PreserveSig]
    int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out ControlFlags capsFlags);

    [PreserveSig]
    int Set(int property, int value, ControlFlags flags);

    [PreserveSig]
    int Get(int property, out int value, out ControlFlags flags);
}

/// <summary>Adapts the two COM interfaces (which cannot share a managed base) to <see cref="IUvcControlInterface"/>.</summary>
internal sealed class CameraControlAdapter(IAMCameraControl control) : IUvcControlInterface
{
    public int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out ControlFlags capsFlags) =>
        control.GetRange(property, out min, out max, out step, out defaultValue, out capsFlags);

    public int Set(int property, int value, ControlFlags flags) => control.Set(property, value, flags);

    public int Get(int property, out int value, out ControlFlags flags) => control.Get(property, out value, out flags);
}

internal sealed class VideoProcAmpAdapter(IAMVideoProcAmp control) : IUvcControlInterface
{
    public int GetRange(int property, out int min, out int max, out int step, out int defaultValue, out ControlFlags capsFlags) =>
        control.GetRange(property, out min, out max, out step, out defaultValue, out capsFlags);

    public int Set(int property, int value, ControlFlags flags) => control.Set(property, value, flags);

    public int Get(int property, out int value, out ControlFlags flags) => control.Get(property, out value, out flags);
}
