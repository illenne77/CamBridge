using System.Globalization;
using CamBridge.Core;

namespace CamBridge.App.ViewModels;

/// <summary>Text conversion for generic parameter editing, following the value types of <see cref="ICamera"/>.</summary>
public static class ParameterValues
{
    public const string AutoOff = "Off";
    public const string AutoContinuous = "Continuous";

    /// <summary>Parses user input into the CLR type the camera expects for <paramref name="type"/>.</summary>
    /// <exception cref="FormatException">The text is not a valid value of that type.</exception>
    public static object Parse(ParameterType type, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        string trimmed = text.Trim();
        return type switch
        {
            ParameterType.Integer => long.Parse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture),
            ParameterType.Float => double.Parse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture),
            ParameterType.Boolean => bool.Parse(trimmed),
            ParameterType.Enumeration or ParameterType.String => trimmed,
            _ => throw new FormatException($"{type} parameters have no value."),
        };
    }

    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        double d => d.ToString("0.###", CultureInfo.InvariantCulture),
        float f => f.ToString("0.###", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>"min .. max (step)" for numeric parameters, the entries for enumerations.</summary>
    public static string DescribeRange(CameraParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        if (parameter.EnumEntries.Count > 0)
        {
            return string.Join(" / ", parameter.EnumEntries);
        }

        if (parameter.Minimum is null && parameter.Maximum is null)
        {
            return string.Empty;
        }

        string range = $"{Format(parameter.Minimum)} .. {Format(parameter.Maximum)}";
        return parameter.Increment is double step && step != 1 ? $"{range} (step {Format(step)})" : range;
    }
}
