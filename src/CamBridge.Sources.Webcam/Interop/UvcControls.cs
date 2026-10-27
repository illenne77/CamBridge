using System.Globalization;
using System.Runtime.InteropServices;

namespace CamBridge.Sources.Webcam.Interop;

/// <summary>Static description of one UVC control and the parameter names it is exposed under.</summary>
/// <param name="Name">Parameter name of the value (raw device units).</param>
/// <param name="AutoName">Parameter name of the Off/Continuous switch when the device supports auto mode.</param>
internal sealed record UvcControlSpec(string Name, string AutoName, bool IsCameraControl, int Property, string? Unit = null);

/// <summary>A control the device reported through GetRange, with its range in device units.</summary>
internal sealed record UvcControl(UvcControlSpec Spec, int Minimum, int Maximum, int Step, int Default, bool SupportsAuto, bool SupportsManual)
{
    public string Name => Spec.Name;
}

/// <summary>
/// UVC controls (IAMCameraControl, IAMVideoProcAmp) of one DirectShow filter instance.
/// Must be created, used and disposed on the device thread.
/// </summary>
internal sealed class UvcControls : IDisposable
{
    public const string Exposure = "Exposure";
    public const string Gain = "Gain";

    // Value names follow the DirectShow property names; auto switches use SFNC names where one exists.
    private static readonly UvcControlSpec[] Specs =
    [
        new(Exposure, "ExposureAuto", true, (int)CameraControlProperty.Exposure, "log2 s"),
        new("Focus", "FocusAuto", true, (int)CameraControlProperty.Focus),
        new("Zoom", "ZoomAuto", true, (int)CameraControlProperty.Zoom),
        new("Pan", "PanAuto", true, (int)CameraControlProperty.Pan),
        new("Tilt", "TiltAuto", true, (int)CameraControlProperty.Tilt),
        new("Roll", "RollAuto", true, (int)CameraControlProperty.Roll),
        new("Iris", "IrisAuto", true, (int)CameraControlProperty.Iris),
        new(Gain, "GainAuto", false, (int)VideoProcAmpProperty.Gain),
        new("Brightness", "BrightnessAuto", false, (int)VideoProcAmpProperty.Brightness),
        new("Contrast", "ContrastAuto", false, (int)VideoProcAmpProperty.Contrast),
        new("Hue", "HueAuto", false, (int)VideoProcAmpProperty.Hue),
        new("Saturation", "SaturationAuto", false, (int)VideoProcAmpProperty.Saturation),
        new("Sharpness", "SharpnessAuto", false, (int)VideoProcAmpProperty.Sharpness),
        new("Gamma", "GammaAuto", false, (int)VideoProcAmpProperty.Gamma),
        new("WhiteBalance", "BalanceWhiteAuto", false, (int)VideoProcAmpProperty.WhiteBalance, "K"),
        new("BacklightCompensation", "BacklightCompensationAuto", false, (int)VideoProcAmpProperty.BacklightCompensation),
    ];

    private readonly object _filter;
    private readonly IUvcControlInterface? _cameraControl;
    private readonly IUvcControlInterface? _procAmp;
    private readonly Dictionary<string, UvcControl> _byName = new(StringComparer.Ordinal);

    public UvcControls(object filter)
    {
        _filter = filter;
        _cameraControl = filter is IAMCameraControl cc ? new CameraControlAdapter(cc) : null;
        _procAmp = filter is IAMVideoProcAmp pa ? new VideoProcAmpAdapter(pa) : null;

        var available = new List<UvcControl>();
        foreach (UvcControlSpec spec in Specs)
        {
            IUvcControlInterface? target = spec.IsCameraControl ? _cameraControl : _procAmp;
            if (target?.GetRange(spec.Property, out int min, out int max, out int step, out int def, out ControlFlags caps) == DirectShowNative.S_OK)
            {
                var control = new UvcControl(spec, min, max, step, def, caps.HasFlag(ControlFlags.Auto), caps.HasFlag(ControlFlags.Manual));
                available.Add(control);
                _byName[spec.Name] = control;
            }
        }

        Available = available;
    }

    /// <summary>Controls the device supports, in a stable display order.</summary>
    public IReadOnlyList<UvcControl> Available { get; }

    /// <summary>Spec for every known control, supported or not (for describing unavailable parameters).</summary>
    public static IReadOnlyList<UvcControlSpec> AllSpecs => Specs;

    public UvcControl? Find(string name) => _byName.GetValueOrDefault(name);

    public (int Value, bool IsAuto) Get(UvcControl control)
    {
        int hr = Target(control).Get(control.Spec.Property, out int value, out ControlFlags flags);
        ThrowIfFailed(hr, control, "read");
        return (value, flags.HasFlag(ControlFlags.Auto));
    }

    public void Set(UvcControl control, int value, bool auto)
    {
        int hr = Target(control).Set(control.Spec.Property, value, auto ? ControlFlags.Auto : ControlFlags.Manual);
        ThrowIfFailed(hr, control, "write");
    }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_filter);
    }

    private IUvcControlInterface Target(UvcControl control) =>
        (control.Spec.IsCameraControl ? _cameraControl : _procAmp)
        ?? throw new InvalidOperationException($"{control.Name} interface is not available.");

    private static void ThrowIfFailed(int hr, UvcControl control, string action)
    {
        if (hr != DirectShowNative.S_OK)
        {
            throw new InvalidOperationException(
                string.Create(CultureInfo.InvariantCulture, $"Failed to {action} {control.Name} (HRESULT 0x{hr:X8})."),
                Marshal.GetExceptionForHR(hr));
        }
    }
}
