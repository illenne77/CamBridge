using CamBridge.Inspection;
using OpenCvSharp;

namespace CamBridge.SampleGenerator;

/// <summary>
/// The demo image set committed under samples/part-inspection: mostly good parts with placement
/// jitter, plus typical defects. File names carry the expected verdict (_ok / _ng).
/// </summary>
public static class DemoSampleSet
{
    private sealed record Sample(string Name, bool Fiducial, Point[] Defects, int Radius, Point Offset);

    private static readonly Sample[] Samples = BuildSamples();

    public const string ImagesFolder = "images";
    public const string RecipeFile = "part.recipe.json";
    public const string TemplateFile = "fiducial.png";

    /// <summary>Writes images/, the template and the recipe into <paramref name="outputDirectory"/>.</summary>
    public static IReadOnlyList<string> Write(string outputDirectory)
    {
        string images = Path.Combine(outputDirectory, ImagesFolder);
        Directory.CreateDirectory(images);
        foreach (string stale in Directory.GetFiles(images, "*.png"))
        {
            File.Delete(stale);
        }

        var written = new List<string>();
        for (int i = 0; i < Samples.Length; i++)
        {
            Sample s = Samples[i];
            using Mat image = SampleImages.Create(s.Fiducial, s.Defects, s.Radius, seed: 100 + i, s.Offset);
            string path = Path.Combine(images, $"{i + 1:D3}_{s.Name}.png");
            Cv2.ImWrite(path, image);
            written.Add(path);
        }

        using Mat template = SampleImages.CreateTemplate();
        Cv2.ImWrite(Path.Combine(outputDirectory, TemplateFile), template);
        RecipeSerializer.Save(SampleImages.Recipe(TemplateFile), Path.Combine(outputDirectory, RecipeFile));
        return written;
    }

    private static Sample[] BuildSamples()
    {
        var random = new Random(2026);
        Point Jitter() => new(random.Next(-6, 7), random.Next(-6, 7));
        Sample Ok() => new("ok", true, [], 4, Jitter());

        return
        [
            Ok(), Ok(), Ok(),
            new("ng_spot", true, [new Point(200, 120)], 4, Jitter()),
            Ok(), Ok(),
            new("ok_dust", true, [new Point(240, 90)], 0, Jitter()), // 1 px, below MinBlobArea
            Ok(),
            new("ng_two_spots", true, [new Point(150, 80), new Point(270, 180)], 4, Jitter()),
            Ok(), Ok(),
            new("ok_spot_outside_roi", true, [new Point(60, 200)], 5, Jitter()),
            new("ng_large_stain", true, [new Point(210, 150)], 12, Jitter()),
            Ok(), Ok(),
            new("ng_missing_mark", false, [], 4, default),
            Ok(), Ok(),
            new("ng_edge_spot", true, [new Point(285, 195)], 4, Jitter()),
            Ok(),
        ];
    }
}
