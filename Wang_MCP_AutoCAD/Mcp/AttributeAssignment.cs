using System.Text.Json;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>One attribute position on a block reference (or its definition), as the planner sees it.</summary>
public readonly record struct AttributeSlot(string Tag, bool IsConstant, bool IsMultiline);

/// <summary>
/// Matches a caller's tag → value map against the attributes a block actually has.
///
/// Used by acad_insert_block (unrequested slots get the definition default) and
/// acad_set_block_attributes (unrequested slots stay as they are). Validation is complete and
/// up front: every unknown tag, every constant tag and every misplaced line break is reported
/// in one message, before anything is written — a half-applied attribute edit is exactly the
/// kind of state a caller cannot reason about.
/// </summary>
public static class AttributeAssignment
{
    public const int MaxValueLength = 32000;

    /// <summary>
    /// Reads an optional tag → string map. Absent is success with an empty map. Keys are kept
    /// as sent; matching against tags is case-insensitive in <see cref="TryPlan"/>.
    /// </summary>
    public static bool TryReadValueMap(
        ToolArgs args,
        string name,
        out IReadOnlyDictionary<string, string> values,
        out string error)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        values = result;

        if (!args.TryGetObjectElement(name, out JsonElement element, out bool present, out error))
        {
            return false;
        }

        if (!present)
        {
            return true;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                error = $"Argument \"{name}\" has an empty tag name.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Argument \"{name}\" value for tag \"{property.Name}\" must be a string.";
                return false;
            }

            string value = property.Value.GetString() ?? "";
            if (value.Length > MaxValueLength)
            {
                error = $"Argument \"{name}\" value for tag \"{property.Name}\" is {value.Length} "
                      + $"characters, which exceeds the {MaxValueLength} character limit.";
                return false;
            }

            if (!result.TryAdd(property.Name.Trim(), value))
            {
                error = $"Argument \"{name}\" names tag \"{property.Name}\" more than once "
                      + "(tags are compared case-insensitively).";
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Plans which slot gets which value. <paramref name="valuesBySlot"/> is parallel to
    /// <paramref name="slots"/>; null means "not requested". A tag that appears on several
    /// slots (AutoCAD allows duplicate tags) sets all of them.
    /// </summary>
    public static bool TryPlan(
        IReadOnlyList<AttributeSlot> slots,
        IReadOnlyDictionary<string, string> requested,
        out string?[] valuesBySlot,
        out string error)
    {
        valuesBySlot = new string?[slots.Count];
        error = "";

        List<string> unknown = new();
        List<string> constant = new();
        List<string> lineBreaks = new();

        foreach (KeyValuePair<string, string> pair in requested)
        {
            bool matched = false;
            bool hasLineBreak = pair.Value.Contains('\n') || pair.Value.Contains('\r');

            for (int i = 0; i < slots.Count; i++)
            {
                if (!string.Equals(slots[i].Tag, pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                matched = true;

                if (slots[i].IsConstant)
                {
                    if (!constant.Contains(slots[i].Tag))
                    {
                        constant.Add(slots[i].Tag);
                    }

                    continue;
                }

                if (hasLineBreak && !slots[i].IsMultiline)
                {
                    if (!lineBreaks.Contains(slots[i].Tag))
                    {
                        lineBreaks.Add(slots[i].Tag);
                    }

                    continue;
                }

                valuesBySlot[i] = pair.Value;
            }

            if (!matched)
            {
                unknown.Add(pair.Key);
            }
        }

        if (unknown.Count == 0 && constant.Count == 0 && lineBreaks.Count == 0)
        {
            return true;
        }

        List<string> problems = new();

        if (unknown.Count > 0)
        {
            problems.Add($"unknown tag(s) {Quote(unknown)} — {DescribeSettable(slots)}");
        }

        if (constant.Count > 0)
        {
            problems.Add($"constant tag(s) {Quote(constant)} have the same value in every "
                       + "reference and cannot be set per reference");
        }

        if (lineBreaks.Count > 0)
        {
            problems.Add($"tag(s) {Quote(lineBreaks)} are single-line, but the value contains "
                       + "a line break");
        }

        valuesBySlot = new string?[slots.Count];
        error = $"Nothing was changed: {string.Join("; ", problems)}.";
        return false;
    }

    private static string DescribeSettable(IReadOnlyList<AttributeSlot> slots)
    {
        List<string> settable = new();
        foreach (AttributeSlot slot in slots)
        {
            if (!slot.IsConstant && !settable.Contains(slot.Tag))
            {
                settable.Add(slot.Tag);
            }
        }

        return settable.Count == 0
            ? "this block has no settable attributes"
            : $"settable tags are {Quote(settable)}";
    }

    private static string Quote(IEnumerable<string> values)
    {
        return string.Join(", ", values.Select(v => $"\"{v}\""));
    }
}
