namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// AutoCAD's LineWeight is an enum of discrete values in hundredths of a millimetre, not a
/// free number — assigning an arbitrary width silently fails or throws. This snaps a
/// requested millimetre width to the nearest legal rung so the caller can be told what it
/// actually got.
///
/// Lineweight is independent of INSUNITS: it is always hundredths of a millimetre regardless
/// of the drawing's units, so nothing here goes through the drawing-unit factor.
/// </summary>
public static class LineWeights
{
    /// <summary>
    /// The legal LineWeight enum values, in hundredths of a millimetre, ascending. These are
    /// the positive rungs only; the negative sentinels (ByLayer -1, ByBlock -2, ByLwDefault -3)
    /// are handled separately because they are not widths.
    /// </summary>
    private static readonly int[] Hundredths =
    {
        0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53,
        60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211,
    };

    public const int ByLayer = -1;
    public const int ByBlock = -2;
    public const int ByLineWeightDefault = -3;

    /// <summary>
    /// Snaps <paramref name="mm_Value"/> to the nearest legal lineweight. Returns false for a
    /// negative or non-finite width; the caller should map ByLayer/ByBlock before calling.
    /// </summary>
    public static bool TryNearest(
        double mm_Value,
        out int enumValue,
        out double mm_Snapped,
        out string error)
    {
        enumValue = 0;
        mm_Snapped = 0;
        error = "";

        if (double.IsNaN(mm_Value) || double.IsInfinity(mm_Value))
        {
            error = "Lineweight must be a finite number of millimetres.";
            return false;
        }

        if (mm_Value < 0)
        {
            error = $"Lineweight must not be negative, but was {mm_Value} mm. Use -1 for ByLayer "
                  + "or -2 for ByBlock via the dedicated argument.";
            return false;
        }

        double hundredths = mm_Value * 100.0;

        int best = Hundredths[0];
        double bestDistance = Math.Abs(hundredths - best);

        foreach (int candidate in Hundredths)
        {
            double distance = Math.Abs(hundredths - candidate);

            // Strictly-less keeps the lower rung on an exact tie, which makes the tie-break
            // deterministic and testable rather than dependent on array order alone.
            if (distance < bestDistance)
            {
                best = candidate;
                bestDistance = distance;
            }
        }

        enumValue = best;
        mm_Snapped = best / 100.0;
        return true;
    }

    /// <summary>True when the value is one of the legal rungs, needing no snapping.</summary>
    public static bool IsExact(int hundredths)
    {
        return Array.IndexOf(Hundredths, hundredths) >= 0;
    }
}
