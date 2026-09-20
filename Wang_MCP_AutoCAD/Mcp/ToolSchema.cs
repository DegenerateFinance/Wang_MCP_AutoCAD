using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Builds the object schemas that go in tools/list. Small on purpose: a model reads these
/// descriptions to decide what to pass, so the value is in the wording, not the machinery.
/// </summary>
public sealed class ToolSchema
{
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = new();

    public static ToolSchema Object() => new();

    public ToolSchema Number(string name, string description, bool required = false)
    {
        return Add(name, "number", description, required, exclusiveMinimum: null);
    }

    public ToolSchema PositiveNumber(string name, string description, bool required = false)
    {
        return Add(name, "number", description, required, exclusiveMinimum: 0);
    }

    public ToolSchema Integer(
        string name,
        string description,
        int minimum,
        int maximum,
        int defaultValue)
    {
        JsonObject property = new()
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = minimum,
            ["maximum"] = maximum,
            ["default"] = defaultValue,
        };

        _properties[name] = property;
        return this;
    }

    public ToolSchema String(string name, string description, bool required = false)
    {
        return Add(name, "string", description, required, exclusiveMinimum: null);
    }

    public JsonObject Build()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = _properties.DeepClone(),
            ["required"] = _required.DeepClone(),
            ["additionalProperties"] = false,
        };
    }

    private ToolSchema Add(
        string name,
        string type,
        string description,
        bool required,
        double? exclusiveMinimum)
    {
        JsonObject property = new()
        {
            ["type"] = type,
            ["description"] = description,
        };

        if (exclusiveMinimum is not null)
        {
            property["exclusiveMinimum"] = exclusiveMinimum.Value;
        }

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
