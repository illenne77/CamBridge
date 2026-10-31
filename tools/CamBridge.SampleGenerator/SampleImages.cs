using CamBridge.Inspection;
using OpenCvSharp;

namespace CamBridge.SampleGenerator;

/// <summary>
/// Synthetic part images: a bright textured surface with a fiducial mark (found by template
/// matching) and optional dark spots (found by the blob tool). Generated, so no dataset license applies.
/// </summary>
public static class SampleImages
{
    public const int Width = 320;
    public const int Height = 240;

    public static readonly RoiRect FiducialAt = new(40, 40, 32, 32);
    public static readonly RoiRect FiducialSearchRoi = new(10, 10, 100, 100);
    public static readonly RoiRect DefectRoi = new(130, 60, 160, 140);

    /// <param name="fiducialOffset">Shift of the fiducial from <see cref="FiducialAt"/> (part placement jitter).</param>
    public static Mat Create(
        bool withFiducial = true,
        IEnumerable<Point>? defects = null,
        int defectRadius = 4,
        int seed = 1,
        Point fiducialOffset = default)
    {
        var image = new Mat(Height, Width, MatType.CV_8UC1);
        var random = new Random(seed);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                image.Set(y, x, (byte)(190 + random.Next(-6, 7)));
            }
        }

        if (withFiducial)
        {
            DrawFiducial(image, FiducialAt.X + fiducialOffset.X, FiducialAt.Y + fiducialOffset.Y);
        }

        foreach (Point p in defects ?? [])
        {
            Cv2.Circle(image, p, defectRadius, Scalar.All(40), thickness: -1);
        }

        return image;
    }

    /// <summary>Template cut from a clean sample at the known fiducial location.</summary>
    public static Mat CreateTemplate()
    {
        using Mat clean = Create();
        using var region = new Mat(clean, new Rect(FiducialAt.X, FiducialAt.Y, FiducialAt.Width, FiducialAt.Height));
        return region.Clone();
    }

    public static InspectionRecipe Recipe(string templatePath = "fiducial.png") => new()
    {
        Name = "Sample part",
        Tools =
        [
            new TemplateMatchToolSettings
            {
                Name = "Fiducial",
                Roi = FiducialSearchRoi,
                TemplatePath = templatePath,
                MinScore = 0.8,
            },
            new BlobToolSettings
            {
                Name = "Stain",
                Roi = DefectRoi,
                Threshold = 100,
                Polarity = BlobPolarity.Dark,
                MinBlobArea = 5,
                Count = new ValueRange(Max: 0),
            },
        ],
    };

    private static void DrawFiducial(Mat image, int x, int y)
    {
        // Asymmetric mark (ring + cross + corner tab) so the match has a unique peak.
        Cv2.Rectangle(image, new Rect(x + 2, y + 2, 28, 28), Scalar.All(30), thickness: 3);
        Cv2.Line(image, new Point(x + 16, y + 6), new Point(x + 16, y + 26), Scalar.All(30), 2);
        Cv2.Line(image, new Point(x + 6, y + 16), new Point(x + 26, y + 16), Scalar.All(30), 2);
        Cv2.Rectangle(image, new Rect(x + 5, y + 5, 6, 6), Scalar.All(30), thickness: -1);
    }
}
