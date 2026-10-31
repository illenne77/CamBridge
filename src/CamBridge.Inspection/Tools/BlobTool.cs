using System.Diagnostics;
using OpenCvSharp;

namespace CamBridge.Inspection.Tools;

public sealed class BlobTool(BlobToolSettings settings) : IInspectionTool
{
    public const string Count = "Count";
    public const string TotalArea = "TotalArea";
    public const string MaxArea = "MaxArea";
    public const string ThresholdUsed = "Threshold";

    public string Name => settings.Name;

    public IReadOnlyList<string> MeasurementNames { get; } = [Count, TotalArea, MaxArea, ThresholdUsed];

    public ToolResult Run(Mat gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        long start = Stopwatch.GetTimestamp();
        RoiRect roi = settings.Roi;
        OverlayRect[] roiOverlay = [new OverlayRect(roi, OverlayKind.Roi)];

        if (!roi.FitsWithin(gray.Width, gray.Height))
        {
            return ToolResult.ConfigurationError(Name, $"ROI {roi} is outside the {gray.Width}x{gray.Height} image",
                roiOverlay, Stopwatch.GetElapsedTime(start));
        }

        ThresholdTypes type = settings.Polarity == BlobPolarity.Dark ? ThresholdTypes.BinaryInv : ThresholdTypes.Binary;
        if (settings.ThresholdMode == ThresholdMode.Otsu)
        {
            type |= ThresholdTypes.Otsu;
        }

        using var region = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var binary = new Mat();
        using var labels = new Mat();
        using var stats = new Mat();
        using var centroids = new Mat();

        double thresholdUsed = Cv2.Threshold(region, binary, settings.Threshold, 255, type);
        int labelCount = Cv2.ConnectedComponentsWithStats(binary, labels, stats, centroids, PixelConnectivity.Connectivity8);

        var overlays = new List<OverlayRect>(roiOverlay);
        int count = 0;
        long totalArea = 0;
        int maxArea = 0;
        for (int label = 1; label < labelCount; label++) // label 0 is the background
        {
            int area = stats.At<int>(label, (int)ConnectedComponentsTypes.Area);
            if (area < settings.MinBlobArea)
            {
                continue;
            }

            count++;
            totalArea += area;
            maxArea = Math.Max(maxArea, area);
            overlays.Add(new OverlayRect(
                new RoiRect(
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Left),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Top),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Width),
                    stats.At<int>(label, (int)ConnectedComponentsTypes.Height)).Offset(roi.X, roi.Y),
                OverlayKind.Blob));
        }

        Measurement[] measurements =
        [
            new(Count, count, settings.Count),
            new(TotalArea, totalArea, settings.TotalArea),
            new(MaxArea, maxArea, settings.MaxArea),
            new(ThresholdUsed, thresholdUsed),
        ];
        return ToolResult.FromMeasurements(Name, measurements, overlays, Stopwatch.GetElapsedTime(start));
    }

    public void Dispose()
    {
    }
}
