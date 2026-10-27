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

    public ToolSchema Boolean(string name, string description, bool defaultValue)
    {
        _properties[name] = new JsonObject
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = defaultValue,
        };

        return this;
    }

    /// <summary>
    /// A one-of string. The allowed values go in the enum AND are restated in the description:
    /// some clients do not surface "enum" to the model, and a value list it cannot see is a
    /// value list it cannot obey.
    /// </summary>
    public ToolSchema StringEnum(
        string name,
        string description,
        IReadOnlyList<string> allowed,
        bool required = false,
        string? defaultValue = null)
    {
        JsonArray enumValues = new();
        foreach (string value in allowed)
        {
            enumValues.Add(value);
        }

        JsonObject property = new()
        {
            ["type"] = "string",
            ["description"] = $"{description} One of: {string.Join(", ", allowed)}.",
            ["enum"] = enumValues,
        };

        if (defaultValue is not null)
        {
            property["default"] = defaultValue;
        }

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }

    /// <summary>
    /// A required integer with bounds. Separate from <see cref="Integer"/> because that
    /// overload takes a default and never joins the required list.
    /// </summary>
    public ToolSchema RequiredInteger(string name, string description, int minimum, int maximum)
    {
        _properties[name] = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = minimum,
            ["maximum"] = maximum,
        };

        _required.Add(name);
        return this;
    }

    public ToolSchema NumberInRange(
        string name,
        string description,
        double minimum,
        double maximum,
        bool required = false)
    {
        JsonObject property = new()
        {
            ["type"] = "number",
            ["description"] = description,
            ["minimum"] = minimum,
            ["maximum"] = maximum,
        };

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }

    public ToolSchema StringArray(
        string name,
        string description,
        int minItems,
        int maxItems,
        bool required = false)
    {
        JsonObject property = new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["minItems"] = minItems,
            ["maxItems"] = maxItems,
            ["items"] = new JsonObject { ["type"] = "string" },
        };

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }

    /// <summary>
    /// A point as three sibling number properties, {prefix}_x / _y / _z. Flat rather than a
    /// nested object to match how the existing tools already read their points.
    /// </summary>
    public ToolSchema PointMm(string prefix, string description, bool required = false)
    {
        Number($"{prefix}_x", $"{description} X ordinate, in millimetres.", required);
        Number($"{prefix}_y", $"{description} Y ordinate, in millimetres.", required);

        _properties[$"{prefix}_z"] = new JsonObject
        {
            ["type"] = "number",
            ["description"] = $"{description} Z ordinate, in millimetres. Defaults to 0.",
            ["default"] = 0,
        };

        return this;
    }

    /// <summary>
    /// A polyline vertex list: an array of {mm_x, mm_y, bulge?} objects. No mm_z — a
    /// lightweight polyline is 2D and takes its Z from the polyline's elevation.
    /// </summary>
    public ToolSchema VertexArrayMm(
        string name,
        string description,
        int minItems,
        int maxItems,
        bool required = false)
    {
        JsonObject itemSchema = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["mm_x"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Vertex X ordinate, in millimetres.",
                },
                ["mm_y"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Vertex Y ordinate, in millimetres.",
                },
                ["bulge"] = new JsonObject
                {
                    ["type"] = "number",
                    ["description"] = "Arc bulge for the segment that follows: tan(angle / 4). "
                                    + "0 is a straight segment, 1 a semicircle; positive bulges "
                                    + "curve counter-clockwise. Dimensionless. Defaults to 0.",
                    ["default"] = 0,
                },
            },
            ["required"] = new JsonArray { "mm_x", "mm_y" },
            ["additionalProperties"] = false,
        };

        JsonObject property = new()
        {
            ["type"] = "array",
            ["description"] = description,
            ["minItems"] = minItems,
            ["maxItems"] = maxItems,
            ["items"] = itemSchema,
        };

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }

    /// <summary>
    /// A free-form string-to-string map, for block attribute tag/value pairs.
    /// </summary>
    public ToolSchema StringMap(string name, string description, bool required = false)
    {
        JsonObject property = new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = new JsonObject { ["type"] = "string" },
        };

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }

    /// <summary>
    /// A map whose values may be strings or numbers, for dynamic block properties: a
    /// visibility state is a string, a distance parameter a number.
    /// </summary>
    public ToolSchema ValueMap(string name, string description, bool required = false)
    {
        JsonObject property = new()
        {
            ["type"] = "object",
            ["description"] = description,
            ["additionalProperties"] = new JsonObject { ["type"] = new JsonArray { "string", "number" } },
        };

        return Property(name, property, required);
    }

    /// <summary>
    /// A property whose schema the caller built itself — for structured items like attribute
    /// definitions, where the item schema lives next to the code that reads it.
    /// </summary>
    public ToolSchema Property(string name, JsonObject schema, bool required = false)
    {
        _properties[name] = schema;

        if (required)
        {
            _required.Add(name);
        }

        return this;
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
