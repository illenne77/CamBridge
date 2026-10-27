using CamBridge.Core;

namespace CamBridge.Sources.Pylon;

/// <summary>
/// Maps vendor-neutral names (<see cref="ParameterNames"/>) to the GenICam node a Basler
/// camera actually has. SFNC 2.x cameras (ace 2, USB ace) use the standard names; older GigE
/// cameras such as the ace classic acA640-120gm follow SFNC 1.x and expose e.g.
/// ExposureTimeAbs and GainRaw instead. Pure logic so it is testable without the SDK.
/// </summary>
internal static class FeatureNames
{
    /// <summary>Candidate node names, preferred first.</summary>
    private static readonly Dictionary<string, string[]> Candidates = new(StringComparer.Ordinal)
    {
        [ParameterNames.ExposureTime] = [ParameterNames.ExposureTime, "ExposureTimeAbs"],
        [ParameterNames.Gain] = [ParameterNames.Gain, "GainRaw"],
        [ParameterNames.AcquisitionFrameRate] = [ParameterNames.AcquisitionFrameRate, "AcquisitionFrameRateAbs"],
    };

    /// <summary>Units for nodes whose GenICam description often leaves Unit empty.</summary>
    private static readonly Dictionary<string, string> KnownUnits = new(StringComparer.Ordinal)
    {
        [ParameterNames.ExposureTime] = "us",
        ["ExposureTimeAbs"] = "us",
        [ParameterNames.Gain] = "dB",
        ["GainRaw"] = "raw",
        [ParameterNames.AcquisitionFrameRate] = "Hz",
        ["AcquisitionFrameRateAbs"] = "Hz",
        ["ResultingFrameRate"] = "Hz",
        ["ResultingFrameRateAbs"] = "Hz",
        [ParameterNames.Width] = "px",
        [ParameterNames.Height] = "px",
        [ParameterNames.OffsetX] = "px",
        [ParameterNames.OffsetY] = "px",
    };

    /// <summary>
    /// The node that implements <paramref name="name"/> on this camera, or null when none does.
    /// Names without an alias resolve to themselves if the node exists.
    /// </summary>
    public static string? Resolve(string name, Func<string, bool> nodeExists)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(nodeExists);

        if (Candidates.TryGetValue(name, out string[]? candidates))
        {
            return candidates.FirstOrDefault(nodeExists);
        }

        return nodeExists(name) ? name : null;
    }

    /// <summary>True when <paramref name="node"/> is an SFNC 1.x stand-in for a vendor-neutral name.</summary>
    public static bool IsLegacyAlias(string node) =>
        Candidates.Values.Any(c => c.Skip(1).Contains(node, StringComparer.Ordinal));

    /// <summary>Vendor-neutral names that need resolving.</summary>
    public static IEnumerable<string> AliasedNames => Candidates.Keys;

    public static string? UnitOf(string node, string? describedUnit) =>
        !string.IsNullOrEmpty(describedUnit) ? describedUnit : KnownUnits.GetValueOrDefault(node);
}
