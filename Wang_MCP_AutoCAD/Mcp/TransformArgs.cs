using System.Linq;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Reads acad_transform's mode-dependent arguments into a <see cref="TransformRequest"/>.
///
/// One tool with a mode enum means one flat argument list where each mode uses a different
/// subset. The risk of that shape is a caller sending an argument its mode ignores — say
/// "deg_angle" with mode "scale" — and believing it took effect. So an argument that belongs
/// only to another mode is rejected, not ignored. The schema is built here too, so the
/// advertised names and the names read can never drift apart.
/// </summary>
public static class TransformArgs
{
    public const string ModeMove = "move";
    public const string ModeRotate = "rotate";
    public const string ModeScale = "scale";
    public const string ModeMirror = "mirror";

    private const string From = "mm_from";
    private const string To = "mm_to";
    private const string Base = "mm_base";
    private const string Angle = "deg_angle";
    private const string Factor = "factor";
    private const string LineStart = "mm_line_start";
    private const string LineEnd = "mm_line_end";

    private static readonly string[] PointPrefixes = { From, To, Base, LineStart, LineEnd };
    private static readonly string[] ScalarNames = { Angle, Factor };

    /// <summary>The point prefixes and scalar names each mode consumes.</summary>
    private static readonly Dictionary<string, string[]> OwnArguments = new(StringComparer.Ordinal)
    {
        [ModeMove] = new[] { From, To },
        [ModeRotate] = new[] { Base, Angle },
        [ModeScale] = new[] { Base, Factor },
        [ModeMirror] = new[] { LineStart, LineEnd },
    };

    /// <summary>Adds every mode's arguments to a schema, each labelled with the mode it belongs to.</summary>
    public static ToolSchema AddTo(ToolSchema schema)
    {
        return schema
            .StringEnum("mode", "Which transform to apply.", TransformRequest.ModeNames, required: true)
            .PointMm(From, "mode=move only: the point to move from.")
            .PointMm(To, "mode=move only: the point to move to.")
            .PointMm(Base, "mode=rotate or scale only: the fixed point the rotation or scaling happens about.")
            .Number(Angle, "mode=rotate only: rotation in degrees, counter-clockwise when viewed from above (looking down the Z axis).")
            .Number(Factor, "mode=scale only: uniform scale factor. Dimensionless; 2 doubles, 0.5 halves. Negative values are allowed and also mirror the geometry. Must not be 0.")
            .PointMm(LineStart, "mode=mirror only: first point on the mirror line.")
            .PointMm(LineEnd, "mode=mirror only: second point on the mirror line.");
    }

    public static bool TryRead(
        ToolArgs args,
        out TransformRequest? request,
        out string mode,
        out string error)
    {
        request = null;

        if (!args.TryGetEnum("mode", TransformRequest.ModeNames, null, out mode, out error))
        {
            return false;
        }

        if (!TryRejectForeignArguments(args, mode, out error))
        {
            return false;
        }

        switch (mode)
        {
            case ModeMove:
                return TryReadMove(args, out request, out error);

            case ModeRotate:
                return TryReadRotate(args, out request, out error);

            case ModeScale:
                return TryReadScale(args, out request, out error);

            case ModeMirror:
                return TryReadMirror(args, out request, out error);

            default:
                error = $"Unhandled mode \"{mode}\".";
                return false;
        }
    }

    private static bool TryReadMove(ToolArgs args, out TransformRequest? request, out string error)
    {
        request = null;

        if (!PointArgs.TryReadPointMm(args, From, required: true, out Mm_Point mm_From, out _, out error))
        {
            return false;
        }

        if (!PointArgs.TryReadPointMm(args, To, required: true, out Mm_Point mm_To, out _, out error))
        {
            return false;
        }

        return TransformRequest.TryBuildMove(mm_From, mm_To, out request, out error);
    }

    private static bool TryReadRotate(ToolArgs args, out TransformRequest? request, out string error)
    {
        request = null;

        if (!PointArgs.TryReadPointMm(args, Base, required: true, out Mm_Point mm_Base, out _, out error))
        {
            return false;
        }

        if (!args.TryGetDouble(Angle, out double deg_Angle, out error))
        {
            return false;
        }

        return TransformRequest.TryBuildRotate(mm_Base, deg_Angle, out request, out error);
    }

    private static bool TryReadScale(ToolArgs args, out TransformRequest? request, out string error)
    {
        request = null;

        if (!PointArgs.TryReadPointMm(args, Base, required: true, out Mm_Point mm_Base, out _, out error))
        {
            return false;
        }

        if (!args.TryGetNonZeroDouble(Factor, out double factor, out error))
        {
            return false;
        }

        return TransformRequest.TryBuildScale(mm_Base, factor, out request, out error);
    }

    private static bool TryReadMirror(ToolArgs args, out TransformRequest? request, out string error)
    {
        request = null;

        if (!PointArgs.TryReadPointMm(args, LineStart, required: true, out Mm_Point mm_Start, out _, out error))
        {
            return false;
        }

        if (!PointArgs.TryReadPointMm(args, LineEnd, required: true, out Mm_Point mm_End, out _, out error))
        {
            return false;
        }

        return TransformRequest.TryBuildMirror(mm_Start, mm_End, out request, out error);
    }

    /// <summary>
    /// Fails if any argument that only another mode consumes is present. Names every offender
    /// so a caller fixing its request does not have to retry once per stray argument.
    /// </summary>
    private static bool TryRejectForeignArguments(ToolArgs args, string mode, out string error)
    {
        error = "";
        string[] own = OwnArguments[mode];
        List<string> foreign = new();

        foreach (string prefix in PointPrefixes)
        {
            if (own.Contains(prefix))
            {
                continue;
            }

            foreach (string suffix in new[] { "_x", "_y", "_z" })
            {
                string name = prefix + suffix;
                if (args.HasArgument(name))
                {
                    foreign.Add(name);
                }
            }
        }

        foreach (string name in ScalarNames)
        {
            if (!own.Contains(name) && args.HasArgument(name))
            {
                foreign.Add(name);
            }
        }

        if (foreign.Count == 0)
        {
            return true;
        }

        error = $"Mode \"{mode}\" does not use {string.Join(", ", foreign.Select(name => $"\"{name}\""))}. "
              + $"It takes: {DescribeOwn(own)}. Remove the other arguments, or change the mode.";
        return false;
    }

    private static string DescribeOwn(string[] own)
    {
        List<string> parts = new();
        foreach (string name in own)
        {
            parts.Add(PointPrefixes.Contains(name) ? $"{name}_x/_y/_z" : name);
        }

        return string.Join(", ", parts);
    }
}
