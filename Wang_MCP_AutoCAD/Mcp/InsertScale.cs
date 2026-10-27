namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The scale of a block insertion: either one uniform "scale", or per-axis
/// "scale_x"/"scale_y"/"scale_z" — never both, since mixing them leaves it ambiguous which wins.
/// Factors are dimensionless and must not be 0; negative is legal and mirrors.
/// </summary>
public static class InsertScale
{
    private static readonly string[] AxisNames = { "scale_x", "scale_y", "scale_z" };

    public static bool TryRead(
        ToolArgs args,
        out double scaleX,
        out double scaleY,
        out double scaleZ,
        out string error)
    {
        scaleX = 1;
        scaleY = 1;
        scaleZ = 1;
        error = "";

        bool hasUniform = args.HasArgument("scale");
        bool hasAxis = AxisNames.Any(args.HasArgument);

        if (hasUniform && hasAxis)
        {
            error = "Give either \"scale\" (uniform) or \"scale_x\"/\"scale_y\"/\"scale_z\" (per "
                  + "axis), not both.";
            return false;
        }

        if (hasUniform)
        {
            if (!args.TryGetNonZeroDouble("scale", out double uniform, out error))
            {
                return false;
            }

            scaleX = uniform;
            scaleY = uniform;
            scaleZ = uniform;
            return true;
        }

        return TryReadAxis(args, "scale_x", out scaleX, out error)
            && TryReadAxis(args, "scale_y", out scaleY, out error)
            && TryReadAxis(args, "scale_z", out scaleZ, out error);
    }

    public static bool IsUniform(double scaleX, double scaleY, double scaleZ)
    {
        return scaleX == scaleY && scaleY == scaleZ;
    }

    /// <summary>
    /// The refusal for a non-uniform scale on a block whose definition demands uniform
    /// scaling. Checked up front because AutoCAD would otherwise accept the insertion and
    /// silently ignore the per-axis factors, or reject it mid-transaction.
    /// </summary>
    public static string DescribeUniformRequired(string blockName, double scaleX, double scaleY, double scaleZ)
    {
        return $"Block \"{blockName}\" only allows uniform scaling, but the requested scale was "
             + $"({scaleX}, {scaleY}, {scaleZ}). Use a single \"scale\" instead.";
    }

    private static bool TryReadAxis(ToolArgs args, string name, out double value, out string error)
    {
        value = 1;
        error = "";

        if (!args.HasArgument(name))
        {
            return true;
        }

        return args.TryGetNonZeroDouble(name, out value, out error);
    }
}
