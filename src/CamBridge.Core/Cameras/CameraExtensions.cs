using System.Globalization;

namespace CamBridge.Core;

public static class CameraExtensions
{
    /// <summary>Reads a parameter and converts it to <typeparamref name="T"/> (e.g. Integer to double).</summary>
    public static T GetParameter<T>(this ICamera camera, string name)
    {
        ArgumentNullException.ThrowIfNull(camera);

        object value = camera.GetParameter(name);
        if (value is T typed)
        {
            return typed;
        }

        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>Finds a parameter description by name, or null when the camera has no such parameter.</summary>
    public static CameraParameter? FindParameter(this ICamera camera, string name)
    {
        ArgumentNullException.ThrowIfNull(camera);

        foreach (CameraParameter parameter in camera.GetParameters())
        {
            if (string.Equals(parameter.Name, name, StringComparison.Ordinal))
            {
                return parameter;
            }
        }

        return null;
    }
}
