using System.Text.Json;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Reading points out of a tools/call argument object. Point reading is the single most
/// repeated piece of argument handling across the drawing tools, so it lives here once and
/// is tested once rather than being re-derived per tool.
///
/// Two shapes are supported, deliberately not interchangeable:
///   - a flat triple, "mm_center_x" / "mm_center_y" / "mm_center_z", used for a tool's own
///     named points, matching how acad_draw_circle already reads;
///   - an array of objects, [{"mm_x":0,"mm_y":0,"bulge":0}, ...], used for vertex lists.
/// </summary>
public static class PointArgs
{
    /// <summary>
    /// Reads the {prefix}_x / {prefix}_y / {prefix}_z triple. Z is always optional and
    /// defaults to 0. <paramref name="present"/> distinguishes "absent, and allowed" from
    /// "present and valid", which is what a mutually-exclusive check needs.
    /// </summary>
    public static bool TryReadPointMm(
        ToolArgs args,
        string prefix,
        bool required,
        out Mm_Point value,
        out bool present,
        out string error)
    {
        value = Mm_Point.Origin;
        present = false;
        error = "";

        string nameX = $"{prefix}_x";
        string nameY = $"{prefix}_y";
        string nameZ = $"{prefix}_z";

        bool hasX = args.HasArgument(nameX);
        bool hasY = args.HasArgument(nameY);

        if (!hasX && !hasY)
        {
            if (!required)
            {
                return true;
            }

            error = $"Required arguments \"{nameX}\" and \"{nameY}\" are missing.";
            return false;
        }

        // A half-supplied point is always a mistake, whether or not the point is required.
        if (!hasX || !hasY)
        {
            error = $"Both \"{nameX}\" and \"{nameY}\" are required to specify a point; "
                  + $"only \"{(hasX ? nameX : nameY)}\" was supplied.";
            return false;
        }

        if (!args.TryGetDouble(nameX, out double mm_X, out error))
        {
            return false;
        }

        if (!args.TryGetDouble(nameY, out double mm_Y, out error))
        {
            return false;
        }

        if (!args.TryGetDoubleOrDefault(nameZ, 0, out double mm_Z, out error))
        {
            return false;
        }

        value = new Mm_Point(mm_X, mm_Y, mm_Z);
        present = true;
        return true;
    }

    /// <summary>
    /// Reads an array of vertex objects for a polyline. Only the object form is accepted —
    /// a bare [[0,0],[10,0]] would be ambiguous about the optional Z and could not carry a
    /// bulge, and supporting both doubles the test matrix for no gain.
    ///
    /// A per-vertex "mm_z" is rejected rather than ignored: this builds a lightweight 2D
    /// Polyline, and silently dropping a Z is how a caller ends up believing it drew 3D
    /// geometry. The polyline's Z comes from its elevation instead.
    /// </summary>
    public static bool TryReadVertexListMm(
        ToolArgs args,
        string name,
        int minimumCount,
        int maximumCount,
        out IReadOnlyList<Mm_Vertex> value,
        out string error)
    {
        value = Array.Empty<Mm_Vertex>();
        error = "";

        if (!args.TryGetArrayElement(name, out JsonElement element, out error))
        {
            return false;
        }

        int count = element.GetArrayLength();
        if (count < minimumCount || count > maximumCount)
        {
            error = $"Argument \"{name}\" must contain between {minimumCount} and {maximumCount} "
                  + $"vertices, but contained {count}.";
            return false;
        }

        List<Mm_Vertex> vertices = new(count);
        int index = 0;

        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                error = $"Argument \"{name}\" item {index} must be an object with \"mm_x\" and "
                      + $"\"mm_y\", but was {DescribeKind(item.ValueKind)}.";
                return false;
            }

            if (item.TryGetProperty("mm_z", out JsonElement _))
            {
                error = $"Argument \"{name}\" item {index} supplied \"mm_z\", but a polyline is "
                      + "2D. Use \"mm_elevation\" to set the polyline's Z.";
                return false;
            }

            if (!TryReadVertexOrdinate(item, name, index, "mm_x", required: true, out double mm_X, out error))
            {
                return false;
            }

            if (!TryReadVertexOrdinate(item, name, index, "mm_y", required: true, out double mm_Y, out error))
            {
                return false;
            }

            if (!TryReadVertexOrdinate(item, name, index, "bulge", required: false, out double bulge, out error))
            {
                return false;
            }

            vertices.Add(new Mm_Vertex(mm_X, mm_Y, bulge));
            index++;
        }

        value = vertices;
        return true;
    }

    private static bool TryReadVertexOrdinate(
        JsonElement item,
        string name,
        int index,
        string key,
        bool required,
        out double value,
        out string error)
    {
        value = 0;
        error = "";

        if (!item.TryGetProperty(key, out JsonElement element) || element.ValueKind == JsonValueKind.Null)
        {
            if (!required)
            {
                return true;
            }

            error = $"Argument \"{name}\" item {index} is missing required key \"{key}\".";
            return false;
        }

        if (element.ValueKind != JsonValueKind.Number)
        {
            error = $"Argument \"{name}\" item {index} key \"{key}\" must be a number, but was "
                  + $"{DescribeKind(element.ValueKind)}.";
            return false;
        }

        if (!element.TryGetDouble(out value) || double.IsNaN(value) || double.IsInfinity(value))
        {
            value = 0;
            error = $"Argument \"{name}\" item {index} key \"{key}\" must be a finite number.";
            return false;
        }

        return true;
    }

    private static string DescribeKind(JsonValueKind kind)
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
