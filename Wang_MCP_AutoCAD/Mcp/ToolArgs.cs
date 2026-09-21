using System.Linq;
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

    /// <summary>
    /// A required string. Also rejects empty/whitespace: an empty layer or block name is never
    /// what the caller meant, and catching it here beats an eInvalidInput deep in a transaction.
    /// </summary>
    public bool TryGetString(string name, out string value, out string error)
    {
        value = "";
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"Argument \"{name}\" must be a string, but was {Describe(element.ValueKind)}.";
            return false;
        }

        string? raw = element.GetString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = $"Argument \"{name}\" must not be empty.";
            return false;
        }

        value = raw;
        return true;
    }

    /// <summary>
    /// A boolean with a fallback. There is deliberately no required-bool overload: every
    /// boolean in this surface has a safe default, and a required one is a design smell.
    /// </summary>
    public bool TryGetBoolOrDefault(string name, bool fallback, out bool value, out string error)
    {
        value = fallback;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.True && element.ValueKind != JsonValueKind.False)
        {
            error = $"Argument \"{name}\" must be true or false, but was {Describe(element.ValueKind)}.";
            return false;
        }

        value = element.GetBoolean();
        return true;
    }

    /// <summary>Like <see cref="TryGetIntInRange"/>, but a missing value is an error.</summary>
    public bool TryGetIntRequired(
        string name,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        value = 0;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        return TryReadInt(element, name, minimum, maximum, out value, out error);
    }

    public bool TryGetPositiveDoubleOrDefault(
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

        return TryGetPositiveDouble(name, out value, out error);
    }

    /// <summary>
    /// A scale factor: any finite number except 0. Negative is legal — it mirrors — so
    /// TryGetPositiveDouble would be wrong here.
    /// </summary>
    public bool TryGetNonZeroDouble(string name, out double value, out string error)
    {
        if (!TryGetDouble(name, out value, out error))
        {
            return false;
        }

        if (value == 0)
        {
            error = $"Argument \"{name}\" must not be 0 — a zero scale factor collapses the "
                  + "geometry and AutoCAD rejects it.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// A one-of argument. Matching is case-insensitive but the value handed back is the
    /// canonical casing from <paramref name="allowed"/>, so callers can switch on it with
    /// ordinal comparison. A null <paramref name="fallback"/> makes the argument required.
    /// </summary>
    public bool TryGetEnum(
        string name,
        IReadOnlyList<string> allowed,
        string? fallback,
        out string value,
        out string error)
    {
        value = fallback ?? "";
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            if (fallback is not null)
            {
                return true;
            }

            error = $"Required argument \"{name}\" is missing. It must be one of: {string.Join(", ", allowed)}.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            error = $"Argument \"{name}\" must be a string, but was {Describe(element.ValueKind)}. "
                  + $"It must be one of: {string.Join(", ", allowed)}.";
            return false;
        }

        string? raw = element.GetString();

        foreach (string candidate in allowed)
        {
            if (string.Equals(candidate, raw, StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        error = $"Argument \"{name}\" must be one of: {string.Join(", ", allowed)}. Got \"{raw}\".";
        return false;
    }

    /// <summary>
    /// A required array of non-empty strings. Element errors name the INDEX — with fifty
    /// handles, "one of them is wrong" is useless to a caller trying to correct itself.
    /// </summary>
    public bool TryGetStringArray(
        string name,
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<string> value,
        out string error)
    {
        value = Array.Empty<string>();
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"Argument \"{name}\" must be an array of strings, but was {Describe(element.ValueKind)}.";
            return false;
        }

        int count = element.GetArrayLength();
        if (count < minimumCount || count > maximumCount)
        {
            error = $"Argument \"{name}\" must contain between {minimumCount} and {maximumCount} "
                  + $"items, but contained {count}.";
            return false;
        }

        List<string> items = new(count);
        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"Argument \"{name}\" item {index} must be a string, but was {Describe(item.ValueKind)}.";
                return false;
            }

            string? raw = item.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                error = $"Argument \"{name}\" item {index} must not be empty.";
                return false;
            }

            items.Add(raw);
            index++;
        }

        value = items;
        return true;
    }

    /// <summary>A required array of finite numbers, with the same index-naming discipline.</summary>
    public bool TryGetDoubleArray(
        string name,
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<double> value,
        out string error)
    {
        value = Array.Empty<double>();
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"Argument \"{name}\" must be an array of numbers, but was {Describe(element.ValueKind)}.";
            return false;
        }

        int count = element.GetArrayLength();
        if (count < minimumCount || count > maximumCount)
        {
            error = $"Argument \"{name}\" must contain between {minimumCount} and {maximumCount} "
                  + $"items, but contained {count}.";
            return false;
        }

        List<double> items = new(count);
        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number)
            {
                error = $"Argument \"{name}\" item {index} must be a number, but was {Describe(item.ValueKind)}.";
                return false;
            }

            if (!item.TryGetDouble(out double parsed) || double.IsNaN(parsed) || double.IsInfinity(parsed))
            {
                error = $"Argument \"{name}\" item {index} must be a finite number.";
                return false;
            }

            items.Add(parsed);
            index++;
        }

        value = items;
        return true;
    }

    /// <summary>
    /// Hands back a required argument that must be a JSON array, for callers that need to walk
    /// the elements themselves (vertex lists, attribute maps). Keeps the JsonElement plumbing
    /// in this class rather than leaking _arguments to helpers.
    /// </summary>
    public bool TryGetArrayElement(string name, out JsonElement value, out string error)
    {
        value = default;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"Argument \"{name}\" must be an array, but was {Describe(element.ValueKind)}.";
            return false;
        }

        value = element;
        return true;
    }

    /// <summary>
    /// Hands back an optional argument that must be a JSON object, for tag/value maps such as
    /// block attributes. Absent is success with <paramref name="present"/> false.
    /// </summary>
    public bool TryGetObjectElement(
        string name,
        out JsonElement value,
        out bool present,
        out string error)
    {
        value = default;
        present = false;
        error = "";

        if (!TryGetProperty(name, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            error = $"Argument \"{name}\" must be an object, but was {Describe(element.ValueKind)}.";
            return false;
        }

        value = element;
        present = true;
        return true;
    }

    /// <summary>True when the argument is present and not null.</summary>
    public bool HasArgument(string name)
    {
        return TryGetProperty(name, out JsonElement element) && element.ValueKind != JsonValueKind.Null;
    }

    /// <summary>
    /// Enforces that exactly one of a set of mutually exclusive arguments was supplied.
    /// </summary>
    public bool RequireExactlyOneOf(IReadOnlyList<string> names, out string chosen, out string error)
    {
        chosen = "";
        error = "";

        List<string> present = new();
        foreach (string name in names)
        {
            if (HasArgument(name))
            {
                present.Add(name);
            }
        }

        string quoted = string.Join(" or ", names.Select(n => $"\"{n}\""));

        if (present.Count == 0)
        {
            error = $"One of {quoted} is required.";
            return false;
        }

        if (present.Count > 1)
        {
            error = $"Exactly one of {quoted} may be given; {string.Join(" and ", present.Select(n => $"\"{n}\""))} were supplied.";
            return false;
        }

        chosen = present[0];
        return true;
    }

    /// <summary>
    /// Enforces that at least one of a set of optional arguments was supplied — the guard a
    /// "change these properties" tool needs so an empty call is not silently a no-op.
    /// </summary>
    public bool RequireAtLeastOneOf(IReadOnlyList<string> names, string subject, out string error)
    {
        error = "";

        foreach (string name in names)
        {
            if (HasArgument(name))
            {
                return true;
            }
        }

        error = $"At least one {subject} is required. Supply one or more of: {string.Join(", ", names)}.";
        return false;
    }

    private bool TryReadInt(
        JsonElement element,
        string name,
        int minimum,
        int maximum,
        out int value,
        out string error)
    {
        value = 0;
        error = "";

        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"Argument \"{name}\" must be an integer, but was {Describe(element.ValueKind)}.";
            return false;
        }

        if (!element.TryGetInt32(out value))
        {
            value = 0;
            error = $"Argument \"{name}\" must be a whole number.";
            return false;
        }

        if (value < minimum || value > maximum)
        {
            error = $"Argument \"{name}\" must be between {minimum} and {maximum}, but was {value}.";
            value = 0;
            return false;
        }

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
