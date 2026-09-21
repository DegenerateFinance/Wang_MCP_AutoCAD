using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// The emitted JSON Schema is what a model reads to decide what to send, so its shape is a
/// contract. There was no coverage of ToolSchema at all before this.
/// </summary>
public sealed class ToolSchemaTests
{
    private static JsonArray RequiredOf(JsonObject schema) => (JsonArray)schema["required"]!;

    private static JsonObject PropertiesOf(JsonObject schema) => (JsonObject)schema["properties"]!;

    private static bool IsRequired(JsonObject schema, string name)
    {
        foreach (JsonNode? node in RequiredOf(schema))
        {
            if (node?.GetValue<string>() == name)
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void Build_ProducesAnObjectSchemaWithAdditionalPropertiesFalse()
    {
        JsonObject schema = ToolSchema.Object().Build();

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.False(schema["additionalProperties"]!.GetValue<bool>());
        Assert.NotNull(schema["properties"]);
        Assert.NotNull(schema["required"]);
    }

    [Fact]
    public void Build_MarksRequiredFieldsAndLeavesOptionalOnesOut()
    {
        JsonObject schema = ToolSchema.Object()
            .Number("needed", "A required number.", required: true)
            .Number("optional", "An optional number.")
            .Build();

        Assert.True(IsRequired(schema, "needed"));
        Assert.False(IsRequired(schema, "optional"));
    }

    [Fact]
    public void Build_IsNotAliasedToTheBuilder()
    {
        ToolSchema builder = ToolSchema.Object().Number("a", "First.");
        JsonObject schema = builder.Build();

        builder.Number("b", "Added after Build.");

        Assert.False(PropertiesOf(schema).ContainsKey("b"));
    }

    [Fact]
    public void Boolean_CarriesItsDefault()
    {
        JsonObject schema = ToolSchema.Object().Boolean("closed", "Close the polyline.", false).Build();
        JsonObject property = (JsonObject)PropertiesOf(schema)["closed"]!;

        Assert.Equal("boolean", property["type"]!.GetValue<string>());
        Assert.False(property["default"]!.GetValue<bool>());
    }

    [Fact]
    public void StringEnum_EmitsTheEnumArray()
    {
        JsonObject schema = ToolSchema.Object()
            .StringEnum("mode", "How to transform.", new[] { "move", "rotate" }, required: true)
            .Build();

        JsonObject property = (JsonObject)PropertiesOf(schema)["mode"]!;
        JsonArray allowed = (JsonArray)property["enum"]!;

        Assert.Equal(2, allowed.Count);
        Assert.Equal("move", allowed[0]!.GetValue<string>());
        Assert.True(IsRequired(schema, "mode"));
    }

    /// <summary>
    /// Some clients do not surface "enum" to the model, so the allowed values are restated in
    /// the description. A value list the model cannot see is a value list it cannot obey.
    /// </summary>
    [Fact]
    public void StringEnum_AlsoNamesTheValuesInTheDescription()
    {
        JsonObject schema = ToolSchema.Object()
            .StringEnum("mode", "How to transform.", new[] { "move", "rotate" })
            .Build();

        string description = PropertiesOf(schema)["mode"]!["description"]!.GetValue<string>();

        Assert.Contains("move", description);
        Assert.Contains("rotate", description);
    }

    [Fact]
    public void StringEnum_CarriesADefaultWhenGiven()
    {
        JsonObject schema = ToolSchema.Object()
            .StringEnum("space", "Target space.", new[] { "model" }, defaultValue: "model")
            .Build();

        Assert.Equal("model", PropertiesOf(schema)["space"]!["default"]!.GetValue<string>());
    }

    /// <summary>
    /// The existing Integer overload takes a default and never joins the required list —
    /// which is why RequiredInteger exists as a separate method rather than a flag.
    /// </summary>
    [Fact]
    public void RequiredInteger_ActuallyAppearsInRequired()
    {
        JsonObject schema = ToolSchema.Object()
            .RequiredInteger("count", "How many.", minimum: 1, maximum: 500)
            .Build();

        Assert.True(IsRequired(schema, "count"));
        Assert.Equal(1, PropertiesOf(schema)["count"]!["minimum"]!.GetValue<int>());
        Assert.Equal(500, PropertiesOf(schema)["count"]!["maximum"]!.GetValue<int>());
    }

    [Fact]
    public void Integer_RemainsOptionalAndCarriesItsDefault()
    {
        JsonObject schema = ToolSchema.Object()
            .Integer("limit", "Maximum returned.", minimum: 1, maximum: 1000, defaultValue: 100)
            .Build();

        Assert.False(IsRequired(schema, "limit"));
        Assert.Equal(100, PropertiesOf(schema)["limit"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public void PointMm_ExpandsToThreeSiblingsWithZDefaulted()
    {
        JsonObject schema = ToolSchema.Object()
            .PointMm("mm_center", "Centre.", required: true)
            .Build();

        JsonObject properties = PropertiesOf(schema);

        Assert.True(properties.ContainsKey("mm_center_x"));
        Assert.True(properties.ContainsKey("mm_center_y"));
        Assert.True(properties.ContainsKey("mm_center_z"));

        Assert.True(IsRequired(schema, "mm_center_x"));
        Assert.True(IsRequired(schema, "mm_center_y"));
        Assert.False(IsRequired(schema, "mm_center_z"));

        Assert.Equal(0, properties["mm_center_z"]!["default"]!.GetValue<int>());
    }

    [Fact]
    public void StringArray_CarriesItemTypeAndBounds()
    {
        JsonObject schema = ToolSchema.Object()
            .StringArray("handles", "Entity handles.", minItems: 1, maxItems: 1000, required: true)
            .Build();

        JsonObject property = (JsonObject)PropertiesOf(schema)["handles"]!;

        Assert.Equal("array", property["type"]!.GetValue<string>());
        Assert.Equal("string", property["items"]!["type"]!.GetValue<string>());
        Assert.Equal(1, property["minItems"]!.GetValue<int>());
        Assert.Equal(1000, property["maxItems"]!.GetValue<int>());
        Assert.True(IsRequired(schema, "handles"));
    }

    [Fact]
    public void VertexArrayMm_NestsTheItemObjectSchema()
    {
        JsonObject schema = ToolSchema.Object()
            .VertexArrayMm("mm_vertices", "Polyline vertices.", minItems: 2, maxItems: 5000, required: true)
            .Build();

        JsonObject property = (JsonObject)PropertiesOf(schema)["mm_vertices"]!;
        JsonObject items = (JsonObject)property["items"]!;
        JsonObject itemProperties = (JsonObject)items["properties"]!;

        Assert.Equal("object", items["type"]!.GetValue<string>());
        Assert.True(itemProperties.ContainsKey("mm_x"));
        Assert.True(itemProperties.ContainsKey("mm_y"));
        Assert.True(itemProperties.ContainsKey("bulge"));

        // A lightweight polyline is 2D: a per-vertex Z must not be offered.
        Assert.False(itemProperties.ContainsKey("mm_z"));

        Assert.False(items["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(2, ((JsonArray)items["required"]!).Count);
    }

    [Fact]
    public void NumberInRange_CarriesBothBounds()
    {
        JsonObject schema = ToolSchema.Object()
            .NumberInRange("transparency", "Percent.", 0, 90)
            .Build();

        Assert.Equal(0.0, PropertiesOf(schema)["transparency"]!["minimum"]!.GetValue<double>());
        Assert.Equal(90.0, PropertiesOf(schema)["transparency"]!["maximum"]!.GetValue<double>());
    }

    [Fact]
    public void StringMap_DescribesAStringValuedObject()
    {
        JsonObject schema = ToolSchema.Object()
            .StringMap("attributes", "Tag to value.", required: true)
            .Build();

        JsonObject property = (JsonObject)PropertiesOf(schema)["attributes"]!;

        Assert.Equal("object", property["type"]!.GetValue<string>());
        Assert.Equal("string", property["additionalProperties"]!["type"]!.GetValue<string>());
        Assert.True(IsRequired(schema, "attributes"));
    }

    [Fact]
    public void PositiveNumber_CarriesExclusiveMinimum()
    {
        JsonObject schema = ToolSchema.Object()
            .PositiveNumber("mm_radius", "Radius.", required: true)
            .Build();

        Assert.Equal(0, PropertiesOf(schema)["mm_radius"]!["exclusiveMinimum"]!.GetValue<double>());
    }
}
