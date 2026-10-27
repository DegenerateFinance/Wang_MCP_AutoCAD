namespace Wang_MCP_AutoCAD.Mcp;

public enum TransformKind
{
    Move,
    Rotate,
    Scale,
    Mirror,
}

/// <summary>
/// A validated transform, in millimetres and radians, ready for the Acad side to turn into a
/// Matrix3d with nothing left to decide.
///
/// This type exists to move every decision that does NOT need AutoCAD off the untestable
/// side of the boundary: which arguments a mode requires, whether a mirror line is
/// degenerate, whether a scale factor is usable, and the degrees-to-radians conversion. What
/// remains in Acad is one switch that constructs a matrix — no branching validation.
/// </summary>
public sealed class TransformRequest
{
    private TransformRequest(TransformKind kind)
    {
        Kind = kind;
    }

    public TransformKind Kind { get; }

    /// <summary>Move: the from-point. Rotate/Scale: the base point. Mirror: the line's start.</summary>
    public Mm_Point Mm_Base { get; private init; }

    /// <summary>Move: the to-point. Mirror: the line's end. Unused otherwise.</summary>
    public Mm_Point Mm_Secondary { get; private init; }

    /// <summary>Rotate only. Already converted from the degrees supplied on the wire.</summary>
    public double Rad_Angle { get; private init; }

    /// <summary>Scale only.</summary>
    public double Factor { get; private init; }

    /// <summary>
    /// True when the matrix flips handedness: a mirror, or a scale by a negative factor (which
    /// in 3D is a point inversion). Either one makes text read backwards, because TransformBy
    /// does not consult MIRRTEXT the way the MIRROR command does.
    /// </summary>
    public bool IsReflection => Kind == TransformKind.Mirror || (Kind == TransformKind.Scale && Factor < 0);

    /// <summary>The argument names each mode consumes, for building precise error messages.</summary>
    public static IReadOnlyList<string> ModeNames { get; } = new[] { "move", "rotate", "scale", "mirror" };

    public static bool TryBuildMove(Mm_Point mm_From, Mm_Point mm_To, out TransformRequest? request, out string error)
    {
        request = null;
        error = "";

        if (!AreFinite(mm_From, out error) || !AreFinite(mm_To, out error))
        {
            return false;
        }

        // A zero-length move is a harmless no-op, not an error — a caller computing a
        // displacement that happens to be zero should not have to special-case it.
        request = new TransformRequest(TransformKind.Move)
        {
            Mm_Base = mm_From,
            Mm_Secondary = mm_To,
        };

        return true;
    }

    public static bool TryBuildRotate(
        Mm_Point mm_BasePoint,
        double deg_Angle,
        out TransformRequest? request,
        out string error)
    {
        request = null;
        error = "";

        if (!AreFinite(mm_BasePoint, out error))
        {
            return false;
        }

        if (double.IsNaN(deg_Angle) || double.IsInfinity(deg_Angle))
        {
            error = "Argument \"deg_angle\" must be a finite number of degrees.";
            return false;
        }

        request = new TransformRequest(TransformKind.Rotate)
        {
            Mm_Base = mm_BasePoint,
            Rad_Angle = Units.DegToRad(deg_Angle),
        };

        return true;
    }

    public static bool TryBuildScale(
        Mm_Point mm_BasePoint,
        double factor,
        out TransformRequest? request,
        out string error)
    {
        request = null;
        error = "";

        if (!AreFinite(mm_BasePoint, out error))
        {
            return false;
        }

        if (double.IsNaN(factor) || double.IsInfinity(factor))
        {
            error = "Argument \"factor\" must be a finite number.";
            return false;
        }

        if (factor == 0)
        {
            error = "Argument \"factor\" must not be 0 — a zero scale factor collapses the "
                  + "geometry and AutoCAD rejects it.";
            return false;
        }

        request = new TransformRequest(TransformKind.Scale)
        {
            Mm_Base = mm_BasePoint,
            Factor = factor,
        };

        return true;
    }

    public static bool TryBuildMirror(
        Mm_Point mm_LineStart,
        Mm_Point mm_LineEnd,
        out TransformRequest? request,
        out string error)
    {
        request = null;
        error = "";

        if (!AreFinite(mm_LineStart, out error) || !AreFinite(mm_LineEnd, out error))
        {
            return false;
        }

        if (mm_LineStart == mm_LineEnd)
        {
            error = "The mirror line's two points are identical; they must define a line.";
            return false;
        }

        request = new TransformRequest(TransformKind.Mirror)
        {
            Mm_Base = mm_LineStart,
            Mm_Secondary = mm_LineEnd,
        };

        return true;
    }

    private static bool AreFinite(Mm_Point point, out string error)
    {
        error = "";

        if (IsBad(point.Mm_X) || IsBad(point.Mm_Y) || IsBad(point.Mm_Z))
        {
            error = "Point ordinates must be finite numbers.";
            return false;
        }

        return true;
    }

    private static bool IsBad(double value) => double.IsNaN(value) || double.IsInfinity(value);
}
