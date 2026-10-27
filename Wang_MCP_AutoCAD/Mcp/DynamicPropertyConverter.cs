using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The CLR type a dynamic block property's Value carries. The Acad side derives this from the
/// boxed type of the property's CURRENT value (double, int, short, string) rather than from
/// PropertyTypeCode: the boxed type is exactly what a new Value must match, and it needs no
/// knowledge of DwgDataType's integer values.
/// </summary>
public enum DynamicValueKind
{
    Real,
    Integer,
    Short,
    Text,
    Unsupported,
}

/// <summary>Mirror of DynamicBlockReferencePropertyUnitsType, mapped by name on the Acad side.</summary>
public enum DynamicUnitsKind
{
    None,
    Distance,
    Angle,
    Area,
}

/// <summary>
/// Converting dynamic block property values between the wire (millimetres, degrees, mm²) and
/// the database (drawing units, radians, du²), and validating them against the property's
/// allowed-value list.
///
/// Value is typed object in the managed API, and assigning the wrong boxed type — an int to a
/// double property, or a distance in millimetres to a drawing in metres — either throws
/// eInvalidInput mid-transaction or silently produces a block 1000× the intended size. Doing
/// the conversion here, Autodesk-free, puts both of those mistakes under test.
/// </summary>
public static class DynamicPropertyConverter
{
    /// <summary>Relative tolerance for matching a real value against an allowed-value list.</summary>
    private const double RelativeTolerance = 1e-9;

    /// <summary>
    /// Reads the required property-name → value map. Values stay as JsonElements because
    /// which conversion applies depends on the property, which only the Acad side knows.
    /// </summary>
    public static bool TryReadRequests(
        ToolArgs args,
        string name,
        out IReadOnlyList<KeyValuePair<string, JsonElement>> requests,
        out string error)
    {
        List<KeyValuePair<string, JsonElement>> result = new();
        requests = result;

        if (!args.TryGetObjectElement(name, out JsonElement element, out bool present, out error))
        {
            return false;
        }

        if (!present)
        {
            error = $"Required argument \"{name}\" is missing.";
            return false;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                error = $"Argument \"{name}\" has an empty property name.";
                return false;
            }

            if (property.Value.ValueKind != JsonValueKind.Number && property.Value.ValueKind != JsonValueKind.String)
            {
                error = $"Argument \"{name}\" value for \"{property.Name}\" must be a number or a string.";
                return false;
            }

            if (!seen.Add(property.Name))
            {
                error = $"Argument \"{name}\" names \"{property.Name}\" more than once (names are "
                      + "compared case-insensitively).";
                return false;
            }

            result.Add(new KeyValuePair<string, JsonElement>(property.Name, property.Value.Clone()));
        }

        if (result.Count == 0)
        {
            error = $"Argument \"{name}\" must name at least one property.";
            return false;
        }

        return true;
    }

    public static string KindName(DynamicValueKind kind)
    {
        return kind switch
        {
            DynamicValueKind.Real => "real",
            DynamicValueKind.Integer => "integer",
            DynamicValueKind.Short => "integer",
            DynamicValueKind.Text => "text",
            _ => "other",
        };
    }

    /// <summary>The unit a property's wire value is denominated in.</summary>
    public static string UnitsName(DynamicUnitsKind units)
    {
        return units switch
        {
            DynamicUnitsKind.Distance => "mm",
            DynamicUnitsKind.Angle => "deg",
            DynamicUnitsKind.Area => "mm2",
            _ => "none",
        };
    }

    /// <summary>Renders a database value for the wire. Never throws: a read must not fail.</summary>
    public static JsonNode? ToWire(
        DynamicValueKind kind,
        DynamicUnitsKind units,
        object? dbValue,
        double mmPerDrawingUnit)
    {
        switch (kind)
        {
            case DynamicValueKind.Real when dbValue is double real:
                return RealToWire(units, real, mmPerDrawingUnit);

            case DynamicValueKind.Integer when dbValue is int integer:
                return integer;

            case DynamicValueKind.Short when dbValue is short shortValue:
                return (int)shortValue;

            case DynamicValueKind.Text when dbValue is string text:
                return text;

            default:
                return dbValue is null ? null : Convert.ToString(dbValue, CultureInfo.InvariantCulture);
        }
    }

    public static double RealToWire(DynamicUnitsKind units, double dbValue, double mmPerDrawingUnit)
    {
        return units switch
        {
            DynamicUnitsKind.Distance => Units.DrawingUnitsToMm(dbValue, mmPerDrawingUnit),
            DynamicUnitsKind.Angle => Units.RadToDeg(dbValue),
            DynamicUnitsKind.Area => Units.DrawingUnits2ToMm2(dbValue, mmPerDrawingUnit),
            _ => dbValue,
        };
    }

    public static double RealToDatabase(DynamicUnitsKind units, double wireValue, double mmPerDrawingUnit)
    {
        return units switch
        {
            DynamicUnitsKind.Distance => Units.MmToDrawingUnits(wireValue, mmPerDrawingUnit),
            DynamicUnitsKind.Angle => Units.DegToRad(wireValue),
            DynamicUnitsKind.Area => Units.Mm2ToDrawingUnits2(wireValue, mmPerDrawingUnit),
            _ => wireValue,
        };
    }

    /// <summary>
    /// Converts a wire value to the boxed database value the property expects. When the
    /// property has an allowed-value list, the result is the matching allowed value itself
    /// (so a 100.0000000001 mm request snaps to the exact stored 100), and anything else is
    /// refused with the list in wire units.
    /// </summary>
    public static bool TryConvert(
        string propertyName,
        DynamicValueKind kind,
        DynamicUnitsKind units,
        JsonElement value,
        double mmPerDrawingUnit,
        IReadOnlyList<object> allowedDbValues,
        out object dbValue,
        out string error)
    {
        dbValue = null!;
        error = "";
        string where = $"Property \"{propertyName}\"";

        switch (kind)
        {
            case DynamicValueKind.Real:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double wire)
                    || double.IsNaN(wire) || double.IsInfinity(wire))
                {
                    error = $"{where} takes a number in {UnitsName(units)}.";
                    return false;
                }

                double converted = RealToDatabase(units, wire, mmPerDrawingUnit);
                return TryMatchAllowedReal(where, units, converted, mmPerDrawingUnit, allowedDbValues, out dbValue, out error);
            }

            case DynamicValueKind.Integer:
            case DynamicValueKind.Short:
            {
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int integer))
                {
                    error = $"{where} takes a whole number.";
                    return false;
                }

                if (kind == DynamicValueKind.Short && (integer < short.MinValue || integer > short.MaxValue))
                {
                    error = $"{where} takes a whole number between {short.MinValue} and {short.MaxValue}.";
                    return false;
                }

                // Not a conditional expression: "cond ? (short)x : x" has type int, so the short
                // would be widened back to int before boxing — and an int assigned to a short
                // property is rejected by AutoCAD. Box each branch explicitly.
                object boxed;
                if (kind == DynamicValueKind.Short)
                {
                    boxed = (short)integer;
                }
                else
                {
                    boxed = integer;
                }
                return TryMatchAllowedExact(where, boxed, allowedDbValues, out dbValue, out error);
            }

            case DynamicValueKind.Text:
            {
                if (value.ValueKind != JsonValueKind.String)
                {
                    error = $"{where} takes a string.";
                    return false;
                }

                return TryMatchAllowedText(where, value.GetString() ?? "", allowedDbValues, out dbValue, out error);
            }

            default:
                error = $"{where} has a value type this server cannot set.";
                return false;
        }
    }

    private static bool TryMatchAllowedReal(
        string where,
        DynamicUnitsKind units,
        double converted,
        double mmPerDrawingUnit,
        IReadOnlyList<object> allowed,
        out object dbValue,
        out string error)
    {
        dbValue = converted;
        error = "";

        if (allowed.Count == 0)
        {
            return true;
        }

        foreach (object candidate in allowed)
        {
            if (candidate is double real
                && Math.Abs(real - converted) <= RelativeTolerance * Math.Max(1.0, Math.Abs(real)))
            {
                dbValue = real;
                return true;
            }
        }

        List<string> wireValues = new();
        foreach (object candidate in allowed)
        {
            if (candidate is double real)
            {
                wireValues.Add(RealToWire(units, real, mmPerDrawingUnit).ToString("0.######", CultureInfo.InvariantCulture));
            }
        }

        error = $"{where} only allows: {string.Join(", ", wireValues)} ({UnitsName(units)}).";
        return false;
    }

    private static bool TryMatchAllowedExact(
        string where,
        object boxed,
        IReadOnlyList<object> allowed,
        out object dbValue,
        out string error)
    {
        dbValue = boxed;
        error = "";

        if (allowed.Count == 0 || allowed.Any(candidate => Equals(candidate, boxed)))
        {
            return true;
        }

        error = $"{where} only allows: {string.Join(", ", allowed.Select(a => Convert.ToString(a, CultureInfo.InvariantCulture)))}.";
        return false;
    }

    private static bool TryMatchAllowedText(
        string where,
        string requested,
        IReadOnlyList<object> allowed,
        out object dbValue,
        out string error)
    {
        dbValue = requested;
        error = "";

        if (allowed.Count == 0)
        {
            return true;
        }

        foreach (object candidate in allowed)
        {
            if (candidate is string text && string.Equals(text, requested, StringComparison.OrdinalIgnoreCase))
            {
                // The stored spelling, not the caller's: a visibility state matched
                // case-insensitively must still be assigned exactly as the block defines it.
                dbValue = text;
                return true;
            }
        }

        error = $"{where} only allows: {string.Join(", ", allowed.Select(a => $"\"{a}\""))}.";
        return false;
    }
}
