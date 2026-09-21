using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad.Support;

/// <summary>
/// Turns a validated <see cref="TransformRequest"/> into a Matrix3d and applies it.
///
/// There is no validation here on purpose: TransformRequest has already rejected a
/// degenerate mirror line, a zero scale factor and non-finite ordinates, so this file is a
/// switch and two loops. That split is what keeps the decisions on the testable side.
/// </summary>
internal static class TransformApplier
{
    public static Matrix3d BuildMatrix(AcadContext acad, TransformRequest request)
    {
        Point3d du_Base = acad.ToPoint3d(request.Mm_Base);
        Point3d du_Secondary = acad.ToPoint3d(request.Mm_Secondary);

        switch (request.Kind)
        {
            case TransformKind.Move:
                return Matrix3d.Displacement(du_Base.GetVectorTo(du_Secondary));

            case TransformKind.Rotate:
                // Vector3d.ZAxis, not the WCS X axis: a 2D rotation turns about Z, and using
                // the wrong axis silently rotates the geometry out of plane.
                return Matrix3d.Rotation(request.Rad_Angle, Vector3d.ZAxis, du_Base);

            case TransformKind.Scale:
                return Matrix3d.Scaling(request.Factor, du_Base);

            case TransformKind.Mirror:
                return Matrix3d.Mirroring(new Line3d(du_Base, du_Secondary));

            default:
                throw new System.InvalidOperationException(
                    $"Unhandled transform kind {request.Kind}.");
        }
    }

    /// <summary>
    /// Transforms the entities in place. They must already be open for write.
    /// </summary>
    public static void Apply(IReadOnlyList<Entity> entities, Matrix3d matrix)
    {
        foreach (Entity entity in entities)
        {
            entity.TransformBy(matrix);
        }
    }

    /// <summary>
    /// Appends transformed copies of the entities to <paramref name="destination"/> and
    /// returns the new handles.
    ///
    /// GetTransformedCopy rather than Database.DeepCloneObjects: DeepCloneObjects is for
    /// cross-database or cross-owner cloning and hands back an IdMapping to interpret, while
    /// for same-space copies of a known list this is one call with nothing to unpack. It
    /// handles a BlockReference correctly too, since the clone shares the definition's id.
    /// </summary>
    public static List<string> ApplyAsCopy(
        Transaction tr,
        BlockTableRecord destination,
        IReadOnlyList<Entity> entities,
        Matrix3d matrix)
    {
        List<string> newHandles = new(entities.Count);

        foreach (Entity entity in entities)
        {
            Entity clone = entity.GetTransformedCopy(matrix);

            destination.AppendEntity(clone);
            tr.AddNewlyCreatedDBObject(clone, true);

            newHandles.Add(clone.Handle.ToString());
        }

        return newHandles;
    }

    /// <summary>
    /// Counts text entities in a selection. Mirroring does not consult MIRRTEXT, so mirrored
    /// text reads backwards; reporting the count lets a caller notice rather than discover it
    /// visually later. Deliberately only counted, never silently "fixed" — whether backwards
    /// text is wrong is a judgement the caller has to make.
    /// </summary>
    public static int CountTextEntities(IReadOnlyList<Entity> entities)
    {
        int count = 0;

        foreach (Entity entity in entities)
        {
            if (entity is DBText or MText)
            {
                count++;
            }
        }

        return count;
    }
}
