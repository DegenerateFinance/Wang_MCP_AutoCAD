using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// One attribute definition to add to a block, as it arrives on the wire. Position is in
/// world millimetres — the same frame as the source entities — and the Acad side shifts it by
/// the base point exactly as it shifts the geometry, so a caller never has to think in block
/// coordinates.
/// </summary>
public sealed record AttributeSpec(
    string Tag,
    string Prompt,
    string DefaultValue,
    Mm_Point Mm_Position,
    double Mm_Height,
    string Justify,
    int AttachmentPoint,
    bool IsConstant,
    bool IsInvisible);

/// <summary>
/// Reads the "attributes" argument of acad_create_block / acad_redefine_block, and builds its
/// schema, in one place so the advertised keys and the read keys cannot drift apart.
/// </summary>
public static class AttributeSpecReader
{
    public const int MaxAttributes = 100;

    private static readonly string[] KnownKeys =
    {
        "tag", "prompt", "default", "mm_x", "mm_y", "mm_z", "mm_height", "justify", "constant", "invisible",
    };

    public static JsonObject BuildSchema(string description)
    {
        JsonObject item = new()
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["tag"] = new JsonObject { ["type"] = "string", ["description"] = "Attribute tag. No spaces; stored uppercase, as AutoCAD does. Must be unique within the block." },
                ["prompt"] = new JsonObject { ["type"] = "string", ["description"] = "Prompt shown when inserting interactively. Defaults to the tag." },
                ["default"] = new JsonObject { ["type"] = "string", ["description"] = "Default value, used when an insertion does not supply one. Defaults to empty." },
                ["mm_x"] = new JsonObject { ["type"] = "number", ["description"] = "Text position X in world millimetres, in the same frame as the block's source entities." },
                ["mm_y"] = new JsonObject { ["type"] = "number", ["description"] = "Text position Y in world millimetres." },
                ["mm_z"] = new JsonObject { ["type"] = "number", ["description"] = "Text position Z in world millimetres. Defaults to 0." },
                ["mm_height"] = new JsonObject { ["type"] = "number", ["exclusiveMinimum"] = 0, ["description"] = "Text height in millimetres." },
                ["justify"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = $"Which point of the text sits at the position. One of: {string.Join(", ", TextJustification.Allowed)}. Defaults to left.",
                    ["enum"] = ToJsonArray(TextJustification.Allowed),
                },
                ["constant"] = new JsonObject { ["type"] = "boolean", ["description"] = "A constant attribute has the same value in every reference and cannot be changed per reference. Defaults to false." },
                ["invisible"] = new JsonObject { ["type"] = "boolean", ["description"] = "Hidden in the drawing (still readable). Defaults to false." },
            },
            ["required"] = new JsonArray { "tag", "mm_x", "mm_y", "mm_height" },
            ["additionalProperties"] = false,
        };

        return new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["maxItems"] = MaxAttributes,
            ["items"] = item,
        };
    }

    /// <summary>
    /// Reads the optional attribute list. Absent is success with an empty list. Every error
    /// names the item index and key, so a caller with twenty attributes knows which to fix.
    /// </summary>
    public static bool TryReadList(
        ToolArgs args,
        string name,
        out IReadOnlyList<AttributeSpec> specs,
        out string error)
    {
        specs = Array.Empty<AttributeSpec>();
        error = "";

        if (!args.HasArgument(name))
        {
            return true;
        }

        if (!args.TryGetArrayElement(name, out JsonElement array, out error))
        {
            return false;
        }

        int count = array.GetArrayLength();
        if (count > MaxAttributes)
        {
            error = $"Argument \"{name}\" has {count} attributes; the maximum is {MaxAttributes}.";
            return false;
        }

        List<AttributeSpec> result = new(count);
        HashSet<string> seenTags = new(StringComparer.Ordinal);
        int index = 0;

        foreach (JsonElement item in array.EnumerateArray())
        {
            if (!TryReadItem(item, name, index, out AttributeSpec? spec, out error))
            {
                return false;
            }

            if (!seenTags.Add(spec!.Tag))
            {
                error = $"Argument \"{name}\" item {index}: tag \"{spec.Tag}\" is used more than once. "
                      + "Tags must be unique within a block (compared case-insensitively).";
                return false;
            }

            result.Add(spec);
            index++;
        }

        specs = result;
        return true;
    }

    /// <summary>
    /// Validates and normalises a tag: trimmed, uppercased (AutoCAD's ATTDEF does the same),
    /// and free of spaces, which AutoCAD rejects in a tag.
    /// </summary>
    public static bool TryNormalizeTag(string? raw, out string tag, out string error)
    {
        tag = "";
        error = "";

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "the tag must not be empty";
            return false;
        }

        string trimmed = raw.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
        {
            error = $"tag \"{trimmed}\" contains a space, which AutoCAD does not allow in an attribute tag";
            return false;
        }

        tag = trimmed.ToUpperInvariant();
        return true;
    }

    private static bool TryReadItem(
        JsonElement item,
        string name,
        int index,
        out AttributeSpec? spec,
        out string error)
    {
        spec = null;
        error = "";
        string where = $"Argument \"{name}\" item {index}";

        if (item.ValueKind != JsonValueKind.Object)
        {
            error = $"{where} must be an object with at least \"tag\", \"mm_x\", \"mm_y\" and \"mm_height\".";
            return false;
        }

        foreach (JsonProperty property in item.EnumerateObject())
        {
            if (!KnownKeys.Contains(property.Name, StringComparer.Ordinal))
            {
                error = $"{where} has unknown key \"{property.Name}\". Allowed keys: {string.Join(", ", KnownKeys)}.";
                return false;
            }
        }

        if (!TryReadString(item, "tag", required: true, out string? rawTag, out error, where))
        {
            return false;
        }

        if (!TryNormalizeTag(rawTag, out string tag, out string tagError))
        {
            error = $"{where}: {tagError}.";
            return false;
        }

        if (!TryReadString(item, "prompt", required: false, out string? prompt, out error, where)
            || !TryReadString(item, "default", required: false, out string? defaultValue, out error, where))
        {
            return false;
        }

        if (!TryReadNumber(item, "mm_x", required: true, out double mm_X, out error, where)
            || !TryReadNumber(item, "mm_y", required: true, out double mm_Y, out error, where)
            || !TryReadNumber(item, "mm_z", required: false, out double mm_Z, out error, where)
            || !TryReadNumber(item, "mm_height", required: true, out double mm_Height, out error, where))
        {
            return false;
        }

        if (mm_Height <= 0)
        {
            error = $"{where}: \"mm_height\" must be greater than 0, but was {mm_Height}.";
            return false;
        }

        if (!TryReadString(item, "justify", required: false, out string? justify, out error, where))
        {
            return false;
        }

        string justifyName = justify ?? TextJustification.DefaultJustification;
        if (!TextJustification.TryGetAttachmentPoint(justifyName, out int attachmentPoint, out string justifyError))
        {
            error = $"{where}: {justifyError}";
            return false;
        }

        if (!TryReadBool(item, "constant", out bool isConstant, out error, where)
            || !TryReadBool(item, "invisible", out bool isInvisible, out error, where))
        {
            return false;
        }

        string value = defaultValue ?? "";

        if (value.Contains('\n') || value.Contains('\r'))
        {
            error = $"{where}: \"default\" contains a line break. Attribute definitions created "
                  + "by this server are single-line.";
            return false;
        }

        // A constant attribute can never be changed per reference, so one with no value
        // displays nothing for ever — never what a caller meant.
        if (isConstant && value.Length == 0)
        {
            error = $"{where}: a constant attribute needs a non-empty \"default\"; its value can "
                  + "never be changed afterwards.";
            return false;
        }

        spec = new AttributeSpec(
            Tag: tag,
            Prompt: string.IsNullOrWhiteSpace(prompt) ? tag : prompt,
            DefaultValue: value,
            Mm_Position: new Mm_Point(mm_X, mm_Y, mm_Z),
            Mm_Height: mm_Height,
            Justify: justifyName.Trim().ToLowerInvariant(),
            AttachmentPoint: attachmentPoint,
            IsConstant: isConstant,
            IsInvisible: isInvisible);

        return true;
    }

    private static bool TryReadString(
        JsonElement item,
        string key,
        bool required,
        out string? value,
        out string error,
        string where)
    {
        value = null;
        error = "";

        if (!item.TryGetProperty(key, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"{where} is missing required key \"{key}\".";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"{where} key \"{key}\" must be a string.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    private static bool TryReadNumber(
        JsonElement item,
        string key,
        bool required,
        out double value,
        out string error,
        string where)
    {
        value = 0;
        error = "";

        if (!item.TryGetProperty(key, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            if (required)
            {
                error = $"{where} is missing required key \"{key}\".";
                return false;
            }

            return true;
        }

        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetDouble(out value)
            || double.IsNaN(value)
            || double.IsInfinity(value))
        {
            value = 0;
            error = $"{where} key \"{key}\" must be a finite number.";
            return false;
        }

        return true;
    }

    private static bool TryReadBool(
        JsonElement item,
        string key,
        out bool value,
        out string error,
        string where)
    {
        value = false;
        error = "";

        if (!item.TryGetProperty(key, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
        {
            error = $"{where} key \"{key}\" must be true or false.";
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    private static JsonArray ToJsonArray(IReadOnlyList<string> values)
    {
        JsonArray array = new();
        foreach (string value in values)
        {
            array.Add(value);
        }

        return array;
    }
}
