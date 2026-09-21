using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad.Support;

/// <summary>
/// Renders an entity as JSON. Every length comes back in millimetres and every angle in
/// degrees, so a caller can compare what it reads with what it sent without knowing the
/// drawing's units.
///
/// "summary" omits the per-type geometry block: listing five hundred entities should not
/// serialise every vertex of every polyline.
/// </summary>
internal static class EntityDescriber
{
    public const string DetailSummary = "summary";
    public const string DetailFull = "full";

    public static IReadOnlyList<string> DetailLevels { get; } = new[] { DetailSummary, DetailFull };

    /// <summary>A polyline with more vertices than this reports a truncated vertex list.</summary>
    private const int MaxReportedVertices = 500;

    public static JsonObject Describe(AcadContext acad, Entity entity, string detail)
    {
        JsonObject description = new()
        {
            ["handle"] = entity.Handle.ToString(),
            ["type"] = entity.GetRXClass().DxfName,
            ["layer"] = entity.Layer,
        };

        AddCommonProperties(description, entity);
        AddExtents(acad, description, entity);

        if (string.Equals(detail, DetailFull, StringComparison.Ordinal))
        {
            AddTypeSpecifics(acad, description, entity);
        }

        return description;
    }

    private static void AddCommonProperties(JsonObject description, Entity entity)
    {
        // ColorIndex 256 is ByLayer and 0 is ByBlock, so a bare integer is unreadable on its
        // own — the display string is what makes the value meaningful.
        description["color_index"] = entity.ColorIndex;
        description["color"] = entity.Color.ToString();
        description["linetype"] = entity.Linetype;
        description["linetype_scale"] = entity.LinetypeScale;
        description["visible"] = entity.Visible;

        try
        {
            description["mm_lineweight"] = Units.LineWeightToMm((int)entity.LineWeight);
        }
        catch (System.Exception)
        {
            // A ByLayer/ByBlock lineweight has no millimetre value of its own.
            description["mm_lineweight"] = null;
        }
    }

    private static void AddExtents(AcadContext acad, JsonObject description, Entity entity)
    {
        // GeometricExtents throws for degenerate entities (an empty MText, a zero-vertex
        // polyline). One bad entity must not fail the whole query, so this stays caught and
        // stays broad — the failure modes are not all AutoCAD's own exception type.
        try
        {
            Extents3d du_Extents = entity.GeometricExtents;
            description["mm_min"] = acad.ToMmArray(du_Extents.MinPoint);
            description["mm_max"] = acad.ToMmArray(du_Extents.MaxPoint);
        }
        catch (System.Exception)
        {
            description["mm_min"] = null;
            description["mm_max"] = null;
        }
    }

    private static void AddTypeSpecifics(AcadContext acad, JsonObject description, Entity entity)
    {
        switch (entity)
        {
            case Circle circle:
                DescribeCircle(acad, description, circle);
                break;

            case Arc arc:
                DescribeArc(acad, description, arc);
                break;

            case Line line:
                DescribeLine(acad, description, line);
                break;

            case Polyline polyline:
                DescribePolyline(acad, description, polyline);
                break;

            case MText mText:
                DescribeMText(acad, description, mText);
                break;

            case AttributeDefinition attributeDefinition:
                DescribeAttributeDefinition(acad, description, attributeDefinition);
                break;

            // DBText must come after AttributeDefinition and AttributeReference: both derive
            // from it, so a bare DBText case would swallow them.
            case DBText dbText:
                DescribeDbText(acad, description, dbText);
                break;

            case BlockReference blockReference:
                DescribeBlockReference(acad, description, blockReference);
                break;
        }
    }

    private static void DescribeCircle(AcadContext acad, JsonObject description, Circle circle)
    {
        description["mm_center"] = acad.ToMmArray(circle.Center);
        description["mm_radius"] = acad.ToMm(circle.Radius);
        description["mm_circumference"] = acad.ToMm(circle.Circumference);
        description["mm2_area"] = Units.DrawingUnits2ToMm2(circle.Area, acad.MmPerDrawingUnit);
    }

    private static void DescribeArc(AcadContext acad, JsonObject description, Arc arc)
    {
        description["mm_center"] = acad.ToMmArray(arc.Center);
        description["mm_radius"] = acad.ToMm(arc.Radius);
        description["deg_start_angle"] = Units.NormalizeDeg(Units.RadToDeg(arc.StartAngle));
        description["deg_end_angle"] = Units.NormalizeDeg(Units.RadToDeg(arc.EndAngle));
        description["deg_total_angle"] = Units.RadToDeg(arc.TotalAngle);
        description["mm_length"] = acad.ToMm(arc.Length);
    }

    private static void DescribeLine(AcadContext acad, JsonObject description, Line line)
    {
        description["mm_start"] = acad.ToMmArray(line.StartPoint);
        description["mm_end"] = acad.ToMmArray(line.EndPoint);
        description["mm_length"] = acad.ToMm(line.Length);
        description["deg_angle"] = Units.NormalizeDeg(Units.RadToDeg(line.Angle));
    }

    private static void DescribePolyline(AcadContext acad, JsonObject description, Polyline polyline)
    {
        description["vertex_count"] = polyline.NumberOfVertices;
        description["closed"] = polyline.Closed;
        description["has_bulges"] = polyline.HasBulges;
        description["mm_elevation"] = acad.ToMm(polyline.Elevation);
        description["mm_length"] = acad.ToMm(polyline.Length);

        if (polyline.Closed)
        {
            description["mm2_area"] = Units.DrawingUnits2ToMm2(polyline.Area, acad.MmPerDrawingUnit);
        }

        int reported = Math.Min(polyline.NumberOfVertices, MaxReportedVertices);
        JsonArray vertices = new();

        for (int i = 0; i < reported; i++)
        {
            Point2d du_Point = polyline.GetPoint2dAt(i);
            vertices.Add(new JsonObject
            {
                ["mm_x"] = acad.ToMm(du_Point.X),
                ["mm_y"] = acad.ToMm(du_Point.Y),
                ["bulge"] = polyline.GetBulgeAt(i),
            });
        }

        description["mm_vertices"] = vertices;
        description["vertices_truncated"] = polyline.NumberOfVertices > reported;
    }

    private static void DescribeDbText(AcadContext acad, JsonObject description, DBText text)
    {
        description["text"] = text.TextString;
        description["mm_height"] = acad.ToMm(text.Height);
        description["mm_position"] = acad.ToMmArray(text.Position);
        description["deg_rotation"] = Units.NormalizeDeg(Units.RadToDeg(text.Rotation));
        description["justify"] = text.Justify.ToString();
        description["text_style"] = text.TextStyleName;
        description["width_factor"] = text.WidthFactor;
        description["deg_oblique"] = Units.RadToDeg(text.Oblique);
    }

    private static void DescribeAttributeDefinition(
        AcadContext acad,
        JsonObject description,
        AttributeDefinition attributeDefinition)
    {
        DescribeDbText(acad, description, attributeDefinition);
        description["tag"] = attributeDefinition.Tag;
        description["prompt"] = attributeDefinition.Prompt;
        description["constant"] = attributeDefinition.Constant;
        description["invisible"] = attributeDefinition.Invisible;
    }

    private static void DescribeMText(AcadContext acad, JsonObject description, MText mText)
    {
        // Both forms are given deliberately: a caller reading the note wants the plain text,
        // while one about to edit it needs the raw contents with the formatting codes intact.
        description["text"] = mText.Text;
        description["contents"] = mText.Contents;
        description["mm_height"] = acad.ToMm(mText.TextHeight);
        description["mm_width"] = acad.ToMm(mText.Width);
        description["mm_actual_width"] = acad.ToMm(mText.ActualWidth);
        description["mm_actual_height"] = acad.ToMm(mText.ActualHeight);
        description["mm_location"] = acad.ToMmArray(mText.Location);
        description["deg_rotation"] = Units.NormalizeDeg(Units.RadToDeg(mText.Rotation));
        description["attachment"] = mText.Attachment.ToString();
        description["text_style"] = mText.TextStyleName;
    }

    private static void DescribeBlockReference(
        AcadContext acad,
        JsonObject description,
        BlockReference blockReference)
    {
        description["block_name"] = ResolveBlockName(blockReference);
        description["mm_position"] = acad.ToMmArray(blockReference.Position);
        description["deg_rotation"] = Units.NormalizeDeg(Units.RadToDeg(blockReference.Rotation));
        description["is_dynamic"] = blockReference.IsDynamicBlock;

        Scale3d scale = blockReference.ScaleFactors;
        description["scale"] = new JsonArray { scale.X, scale.Y, scale.Z };

        JsonObject attributes = new();
        foreach (ObjectId attributeId in blockReference.AttributeCollection)
        {
            if (attributeId.IsErased)
            {
                continue;
            }

            using AttributeReference? attribute =
                attributeId.GetObject(OpenMode.ForRead) as AttributeReference;

            if (attribute is not null)
            {
                attributes[attribute.Tag] = attribute.TextString;
            }
        }

        description["attributes"] = attributes;
        description["has_attributes"] = attributes.Count > 0;
    }

    /// <summary>
    /// A dynamic block whose properties have been changed reports an anonymous "*U" name via
    /// Name; DynamicBlockTableRecord still points at the real definition, which is the name a
    /// caller can actually insert by.
    /// </summary>
    private static string ResolveBlockName(BlockReference blockReference)
    {
        try
        {
            if (blockReference.IsDynamicBlock)
            {
                using BlockTableRecord? definition =
                    blockReference.DynamicBlockTableRecord.GetObject(OpenMode.ForRead) as BlockTableRecord;

                if (definition is not null)
                {
                    return definition.Name;
                }
            }
        }
        catch (System.Exception)
        {
            // Fall through to the reference's own name.
        }

        return blockReference.Name;
    }
}
