using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace CamBridge.Sources.Webcam.Interop;

/// <summary>One entry of the DirectShow video input category.</summary>
/// <param name="Index">Position in the category; equals the OpenCV CAP_DSHOW camera index.</param>
/// <param name="DevicePath">Stable device interface path (empty for some virtual cameras).</param>
internal sealed record DirectShowDevice(int Index, string FriendlyName, string DevicePath)
{
    /// <summary>Key stored in <see cref="Core.CameraInfo.DeviceId"/>: the device path, or the name when there is none.</summary>
    public string DeviceId => DevicePath.Length > 0 ? DevicePath : FriendlyName;
}

/// <summary>
/// Enumerates video capture devices through ICreateDevEnum. OpenCV's DirectShow backend walks
/// the same category in the same order, so <see cref="DirectShowDevice.Index"/> opens the same device.
/// </summary>
internal static class DirectShowDevices
{
    public static IReadOnlyList<DirectShowDevice> Enumerate()
    {
        var devices = new List<DirectShowDevice>();
        ForEachMoniker((index, moniker) =>
        {
            (string name, string path) = ReadProperties(moniker);
            devices.Add(new DirectShowDevice(index, name, path));
            return false;
        });
        return devices;
    }

    /// <summary>True while a device with this id is in the video input category (cheap: no filter is bound).</summary>
    public static bool IsPresent(string deviceId)
    {
        bool found = false;
        ForEachMoniker((index, moniker) =>
        {
            (string name, string path) = ReadProperties(moniker);
            found = string.Equals(new DirectShowDevice(index, name, path).DeviceId, deviceId, StringComparison.OrdinalIgnoreCase);
            return found;
        });
        return found;
    }

    /// <summary>
    /// Finds the device by id and binds a separate filter instance for its UVC controls.
    /// Returns null when the device is not present. The caller releases the filter with
    /// <see cref="Marshal.ReleaseComObject"/>.
    /// </summary>
    public static (DirectShowDevice Device, object Filter)? Bind(string deviceId)
    {
        (DirectShowDevice, object)? result = null;
        ForEachMoniker((index, moniker) =>
        {
            (string name, string path) = ReadProperties(moniker);
            var device = new DirectShowDevice(index, name, path);
            if (!string.Equals(device.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Guid iid = DirectShowNative.IidBaseFilter;
            moniker.BindToObject(null!, null, ref iid, out object filter);
            result = (device, filter);
            return true;
        });
        return result;
    }

    /// <summary>Visits each moniker until <paramref name="visit"/> returns true; releases every moniker.</summary>
    private static void ForEachMoniker(Func<int, IMoniker, bool> visit)
    {
        Type type = Type.GetTypeFromCLSID(DirectShowNative.SystemDeviceEnum, throwOnError: true)!;
        var deviceEnum = (ICreateDevEnum)Activator.CreateInstance(type)!;
        IEnumMoniker? monikers = null;
        try
        {
            Guid category = DirectShowNative.VideoInputDeviceCategory;
            if (deviceEnum.CreateClassEnumerator(ref category, out monikers, 0) != DirectShowNative.S_OK || monikers is null)
            {
                return; // S_FALSE: no video input devices
            }

            var batch = new IMoniker[1];
            for (int index = 0; monikers.Next(1, batch, IntPtr.Zero) == DirectShowNative.S_OK; index++)
            {
                IMoniker moniker = batch[0];
                try
                {
                    if (visit(index, moniker))
                    {
                        return;
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(moniker);
                }
            }
        }
        finally
        {
            if (monikers is not null)
            {
                Marshal.ReleaseComObject(monikers);
            }

            Marshal.ReleaseComObject(deviceEnum);
        }
    }

    private static (string Name, string Path) ReadProperties(IMoniker moniker)
    {
        Guid iid = DirectShowNative.IidPropertyBag;
        moniker.BindToStorage(null!, null, ref iid, out object bagObject);
        var bag = (IPropertyBag)bagObject;
        try
        {
            string name = bag.Read("FriendlyName", out object? n, IntPtr.Zero) == DirectShowNative.S_OK ? n as string ?? string.Empty : string.Empty;
            string path = bag.Read("DevicePath", out object? p, IntPtr.Zero) == DirectShowNative.S_OK ? p as string ?? string.Empty : string.Empty;
            return (name, path);
        }
        finally
        {
            Marshal.ReleaseComObject(bag);
        }
    }
}
