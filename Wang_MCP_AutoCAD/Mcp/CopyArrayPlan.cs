namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The offsets for acad_copy: <c>count</c> copies, copy <c>i</c> displaced by <c>i</c> times
/// the displacement. Kept Autodesk-free so the bounds and the arithmetic are unit tested.
/// </summary>
public static class CopyArrayPlan
{
    public const int MaxCount = 100;

    /// <summary>
    /// Builds the offsets, one per copy, in millimetres.
    ///
    /// A zero displacement is rejected, unlike a zero-length move: a move by nothing changes
    /// nothing, but a copy by nothing stacks an invisible duplicate exactly on top of the
    /// original, which is one of the most common ways a drawing gets quietly corrupted.
    /// </summary>
    public static bool TryBuild(
        Mm_Point mm_Displacement,
        int count,
        int handleCount,
        int maxNewEntities,
        out IReadOnlyList<Mm_Point> mm_Offsets,
        out string error)
    {
        mm_Offsets = Array.Empty<Mm_Point>();
        error = "";

        if (IsBad(mm_Displacement.Mm_X) || IsBad(mm_Displacement.Mm_Y) || IsBad(mm_Displacement.Mm_Z))
        {
            error = "Displacement ordinates must be finite numbers.";
            return false;
        }

        if (mm_Displacement == Mm_Point.Origin)
        {
            error = "The displacement is zero, which would stack each copy exactly on top of its "
                  + "original. Give a non-zero mm_displacement_x/_y/_z.";
            return false;
        }

        if (count < 1 || count > MaxCount)
        {
            error = $"Argument \"count\" must be between 1 and {MaxCount}, but was {count}.";
            return false;
        }

        long newEntities = (long)count * handleCount;
        if (newEntities > maxNewEntities)
        {
            error = $"{count} copies of {handleCount} entities would create {newEntities} new "
                  + $"entities, which exceeds the {maxNewEntities} limit per call. Split the call.";
            return false;
        }

        List<Mm_Point> offsets = new(count);
        for (int i = 1; i <= count; i++)
        {
            offsets.Add(new Mm_Point(
                mm_Displacement.Mm_X * i,
                mm_Displacement.Mm_Y * i,
                mm_Displacement.Mm_Z * i));
        }

        mm_Offsets = offsets;
        return true;
    }

    private static bool IsBad(double value) => double.IsNaN(value) || double.IsInfinity(value);
}
