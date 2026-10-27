using System.Text.Json;
using System.Text.Json.Serialization;

namespace CamBridge.Inspection;

public static class RecipeSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize(InspectionRecipe recipe) => JsonSerializer.Serialize(recipe, Options);

    public static InspectionRecipe Deserialize(string json)
    {
        InspectionRecipe recipe = JsonSerializer.Deserialize<InspectionRecipe>(json, Options)
            ?? throw new JsonException("Recipe JSON is empty.");
        Validate(recipe);
        return recipe;
    }

    public static void Save(InspectionRecipe recipe, string path)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        Validate(recipe);
        File.WriteAllText(path, Serialize(recipe));
    }

    public static InspectionRecipe Load(string path) => Deserialize(File.ReadAllText(path));

    private static void Validate(InspectionRecipe recipe)
    {
        if (recipe.SchemaVersion != InspectionRecipe.CurrentSchemaVersion)
        {
            throw new NotSupportedException(
                $"Recipe schema version {recipe.SchemaVersion} is not supported (expected {InspectionRecipe.CurrentSchemaVersion}).");
        }

        string? duplicate = recipe.Tools
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1)?.Key;
        if (duplicate is not null)
        {
            throw new InvalidDataException($"Tool name '{duplicate}' is used more than once.");
        }
    }
}
