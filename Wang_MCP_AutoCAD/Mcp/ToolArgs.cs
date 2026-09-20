using System.Text.Json;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Typed, validating access to a tools/call "arguments" object. Every accessor returns a
/// bool and names the offending field in its error message, because that message is fed
/// back to the model as isError content and is what lets it retry correctly.
/// </summary>
public sealed class ToolArgs
{
    private readonly JsonElement _arguments;
    private readonly bool _hasArguments;

    public ToolArgs(JsonElement? arguments)
    {
        if (arguments is null || arguments.Value.ValueKind != JsonValueKind.Object)
        {
            _hasArguments = false;
            _arguments = default;
            return;
        }

        _hasArguments = true;
        _arguments = arguments.Value;
    }

    /// <summary>An empty argument set, for tools that take none.</summary>
    public static ToolArgs Empty => new(null);

    public bool TryGetDouble(string name, out double value, out string error)
    {
        value = 0;
        error = "";

        if (!TryGetProperty(name, out JsonElement element))
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"Argument \"{name}\" must be a number, but was {Describe(element.ValueKind)}.";
            return false;
        }

        if (!element.TryGetDouble(out value))
        {
            error = $"Argument \"{name}\" is not a value this server can represent as a double.";
            return false;
        }

        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            error = $"Argument \"{name}\" must be a finite number.";
            return false;
        }

        return true;
    }

    public bool TryGetDoubleOrDefault(
        string name,
        double fallback,
        out double value,
        out string error)
    {
        value = fallback;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        return TryGetDouble(name, out value, out error);
    }

    public bool TryGetPositiveDouble(string name, out double value, out string error)
    {
        if (!TryGetDouble(name, out value, out error))
        {
            return false;
        }

        if (value <= 0)
        {
            error = $"Argument \"{name}\" must be greater than 0, but was {value}.";
            return false;
        }

        return true;
    }

    public bool TryGetIntInRange(
        string name,
        int fallback,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        value = fallback;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"Argument \"{name}\" must be an integer, but was {Describe(element.ValueKind)}.";
            return false;
        }

        if (!element.TryGetInt32(out value))
        {
            value = fallback;
            error = $"Argument \"{name}\" must be a whole number.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"Argument \"{name}\" must be between {minimum} and {maximum}, but was {value}.";
            value = fallback;
            return false;
        }

        return true;
    }

    public bool TryGetStringOrDefault(
        string name,
        string? fallback,
        out string? value,
        out string error)
    {
        value = fallback;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"Argument \"{name}\" must be a string, but was {Describe(element.ValueKind)}.";
            return false;
        }

        value = element.GetString();
        return true;
    }

    private bool TryGetProperty(string name, out JsonElement element)
    {
        element = default;
        if (!_hasArguments)
        {
            return false;
        }

        return _arguments.TryGetProperty(name, out element);
    }

    private static string Describe(JsonValueKind kind)
    {
        return kind switch
        {
            JsonValueKind.String => "a string",
            JsonValueKind.Number => "a number",
            JsonValueKind.True or JsonValueKind.False => "a boolean",
            JsonValueKind.Array => "an array",
            JsonValueKind.Object => "an object",
            JsonValueKind.Null => "null",
            _ => "undefined",
        };
    }
}
