using System.Diagnostics;
using OpenCvSharp;

namespace CamBridge.Inspection.Tools;

/// <summary>Normalized cross-correlation (TM_CCOEFF_NORMED): score in [-1, 1], robust to brightness offset.</summary>
public sealed class TemplateMatchTool : IInspectionTool
{
    public const string Score = "Score";
    public const string X = "X";
    public const string Y = "Y";

    private readonly TemplateMatchToolSettings _settings;
    private readonly Mat _template;

    /// <param name="template">8-bit grayscale template; ownership passes to the tool.</param>
    public TemplateMatchTool(TemplateMatchToolSettings settings, Mat template)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(template);
        if (template.Empty() || template.Type() != MatType.CV_8UC1)
        {
            throw new ArgumentException("Template must be a non-empty 8-bit grayscale image.", nameof(template));
        }

        _settings = settings;
        _template = template;
    }

    public string Name => _settings.Name;

    public IReadOnlyList<string> MeasurementNames { get; } = [Score, X, Y];

    public ToolResult Run(Mat gray)
    {
        ArgumentNullException.ThrowIfNull(gray);
        long start = Stopwatch.GetTimestamp();
        RoiRect roi = _settings.Roi;
        var overlays = new List<OverlayRect> { new(roi, OverlayKind.Roi) };

        if (!roi.FitsWithin(gray.Width, gray.Height))
        {
            return ToolResult.ConfigurationError(Name, $"ROI {roi} is outside the {gray.Width}x{gray.Height} image",
                overlays, Stopwatch.GetElapsedTime(start));
        }

        if (roi.Width < _template.Width || roi.Height < _template.Height)
        {
            return ToolResult.ConfigurationError(Name, $"ROI {roi} is smaller than the {_template.Width}x{_template.Height} template",
                overlays, Stopwatch.GetElapsedTime(start));
        }

        using var region = new Mat(gray, new Rect(roi.X, roi.Y, roi.Width, roi.Height));
        using var response = new Mat();
        Cv2.MatchTemplate(region, _template, response, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(response, out _, out double maxScore, out _, out Point maxLoc);

        // A featureless region yields an undefined correlation; treat it as no match.
        double score = double.IsFinite(maxScore) ? maxScore : 0;
        var match = new RoiRect(roi.X + maxLoc.X, roi.Y + maxLoc.Y, _template.Width, _template.Height);
        overlays.Add(new OverlayRect(match, OverlayKind.Match));

        Measurement[] measurements =
        [
            new(Score, score, new ValueRange(Min: _settings.MinScore)),
            new(X, match.X),
            new(Y, match.Y),
        ];
        return ToolResult.FromMeasurements(Name, measurements, overlays, Stopwatch.GetElapsedTime(start));
    }

    public void Dispose() => _template.Dispose();
}
