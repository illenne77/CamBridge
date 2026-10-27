using OpenCvSharp;

namespace CamBridge.Inspection.Tools;

/// <summary>A compiled tool ready to run. Implementations must be safe to call from one thread at a time.</summary>
public interface IInspectionTool : IDisposable
{
    string Name { get; }

    /// <summary>Names of the measurements this tool reports, in a fixed order (used for CSV columns).</summary>
    IReadOnlyList<string> MeasurementNames { get; }

    /// <param name="gray">Full 8-bit single-channel image.</param>
    ToolResult Run(Mat gray);
}
