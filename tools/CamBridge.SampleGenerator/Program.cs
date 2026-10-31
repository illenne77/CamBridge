using CamBridge.SampleGenerator;

// Usage: dotnet run --project tools/CamBridge.SampleGenerator [-- <output directory>]
string output = args.Length > 0 ? args[0] : Path.Combine("samples", "part-inspection");
IReadOnlyList<string> files = DemoSampleSet.Write(output);
Console.WriteLine($"Wrote {files.Count} images, {DemoSampleSet.TemplateFile} and {DemoSampleSet.RecipeFile} to {Path.GetFullPath(output)}");
