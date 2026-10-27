using System.Globalization;

namespace CamBridge.Inspection;

/// <summary>Inclusive acceptance range. A null bound is open.</summary>
public sealed record ValueRange(double? Min = null, double? Max = null)
{
    public bool Contains(double value) =>
        !double.IsNaN(value) && (Min is null || value >= Min) && (Max is null || value <= Max);

    /// <summary>Describes why <paramref name="value"/> is outside the range, or null if it is inside.</summary>
    public string? DescribeViolation(string measurement, double value)
    {
        if (double.IsNaN(value))
        {
            return $"{measurement} is not a number";
        }

        if (Min is double min && value < min)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{measurement} {value:0.###} < min {min:0.###}");
        }

        if (Max is double max && value > max)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{measurement} {value:0.###} > max {max:0.###}");
        }

        return null;
    }
}
