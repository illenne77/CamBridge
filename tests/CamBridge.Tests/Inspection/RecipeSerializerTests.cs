using System.Text.Json;
using CamBridge.Inspection;
using CamBridge.SampleGenerator;

namespace CamBridge.Tests.Inspection;

public class RecipeSerializerTests
{
    [Fact]
    public void RoundTrip_PreservesPolymorphicTools()
    {
        InspectionRecipe original = SampleImages.Recipe();

        string json = RecipeSerializer.Serialize(original);
        InspectionRecipe loaded = RecipeSerializer.Deserialize(json);

        Assert.Contains("\"$type\": \"blob\"", json, StringComparison.Ordinal);
        Assert.Contains("\"$type\": \"templateMatch\"", json, StringComparison.Ordinal);
        Assert.Contains("\"polarity\": \"dark\"", json, StringComparison.Ordinal);
        Assert.Equal(original.Name, loaded.Name);
        Assert.Equal(original.Tools, loaded.Tools); // records compare by value
    }

    [Fact]
    public void UnsupportedSchemaVersion_Throws()
    {
        string json = RecipeSerializer.Serialize(SampleImages.Recipe() with { SchemaVersion = 99 });

        Assert.Throws<NotSupportedException>(() => RecipeSerializer.Deserialize(json));
    }

    [Fact]
    public void DuplicateToolNames_Throw()
    {
        InspectionRecipe recipe = SampleImages.Recipe();
        recipe = recipe with { Tools = [recipe.Tools[0], recipe.Tools[0]] };

        Assert.Throws<InvalidDataException>(() => RecipeSerializer.Deserialize(RecipeSerializer.Serialize(recipe)));
    }

    [Fact]
    public void MissingRequiredProperty_Throws()
    {
        const string json = """{ "schemaVersion": 1, "name": "x", "tools": [ { "$type": "blob", "name": "b" } ] }""";

        Assert.Throws<JsonException>(() => RecipeSerializer.Deserialize(json));
    }
}
