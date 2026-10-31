using System.Diagnostics;
using CamBridge.Core;
using CamBridge.Inspection.Tools;
using OpenCvSharp;

namespace CamBridge.Inspection;

/// <summary>
/// Compiled recipe: tools are built once (templates loaded) and run per frame.
/// Not thread-safe; use one engine per consumer thread.
/// </summary>
public sealed class InspectionEngine : IDisposable
{
    private readonly IInspectionTool[] _tools;

    private InspectionEngine(InspectionRecipe recipe, IInspectionTool[] tools)
    {
        Recipe = recipe;
        _tools = tools;
        MeasurementColumns = tools.SelectMany(t => t.MeasurementNames.Select(m => $"{t.Name}.{m}")).ToArray();
    }

    public InspectionRecipe Recipe { get; }

    /// <summary>"Tool.Measurement" names of every enabled tool, in recipe order.</summary>
    public IReadOnlyList<string> MeasurementColumns { get; }

    /// <summary>Loads a recipe file; template paths resolve relative to its directory.</summary>
    public static InspectionEngine Load(string recipePath)
    {
        InspectionRecipe recipe = RecipeSerializer.Load(recipePath);
        return Create(recipe, Path.GetDirectoryName(Path.GetFullPath(recipePath)));
    }

    /// <summary>
    /// Builds the tools. Missing or unreadable template files throw here, because the tool
    /// cannot exist without them; geometry problems are reported per frame as NG instead.
    /// </summary>
    public static InspectionEngine Create(InspectionRecipe recipe, string? baseDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        var tools = new List<IInspectionTool>();
        try
        {
            foreach (ToolSettings settings in recipe.Tools.Where(t => t.Enabled))
            {
                tools.Add(settings switch
                {
                    BlobToolSettings blob => new BlobTool(blob),
                    TemplateMatchToolSettings match => new TemplateMatchTool(match, LoadTemplate(match, baseDirectory)),
                    _ => throw new NotSupportedException($"Tool type {settings.GetType().Name} is not supported."),
                });
            }
        }
        catch
        {
            tools.ForEach(t => t.Dispose());
            throw;
        }

        return new InspectionEngine(recipe, tools.ToArray());
    }

    public InspectionResult Inspect(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return FrameMat.WithGray(frame, gray => Inspect(gray, frame.FrameId, frame.TimestampUtc));
    }

    /// <param name="gray">8-bit single-channel image.</param>
    public InspectionResult Inspect(Mat gray, long frameId, DateTime timestampUtc)
    {
        ArgumentNullException.ThrowIfNull(gray);
        if (gray.Type() != MatType.CV_8UC1)
        {
            throw new ArgumentException("Inspection requires an 8-bit grayscale image.", nameof(gray));
        }

        long start = Stopwatch.GetTimestamp();
        var results = new ToolResult[_tools.Length];
        for (int i = 0; i < _tools.Length; i++)
        {
            results[i] = _tools[i].Run(gray);
        }

        Verdict verdict = results.All(r => r.Passed) ? Verdict.Ok : Verdict.Ng;
        return new InspectionResult(frameId, timestampUtc, verdict, results, Stopwatch.GetElapsedTime(start));
    }

    public void Dispose()
    {
        foreach (IInspectionTool tool in _tools)
        {
            tool.Dispose();
        }
    }

    private static Mat LoadTemplate(TemplateMatchToolSettings settings, string? baseDirectory)
    {
        string path = Path.IsPathRooted(settings.TemplatePath) || baseDirectory is null
            ? settings.TemplatePath
            : Path.Combine(baseDirectory, settings.TemplatePath);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Template image for tool '{settings.Name}' not found.", path);
        }

        // Read through .NET file APIs: Cv2.ImRead marshals the path through the ANSI code page and
        // rejects characters outside it. ImDecode throws on an empty buffer.
        byte[] encoded = File.ReadAllBytes(path);
        Mat template = encoded.Length == 0 ? new Mat() : Cv2.ImDecode(encoded, ImreadModes.Grayscale);
        if (template.Empty())
        {
            template.Dispose();
            throw new InvalidDataException($"Template image for tool '{settings.Name}' could not be decoded: {path}");
        }

        return template;
    }
}
