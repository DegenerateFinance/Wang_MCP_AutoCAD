using System.Text.Json.Nodes;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad.Support;

/// <summary>
/// The preamble every database tool shares: the active document, its database, and the
/// INSUNITS-derived conversion factor.
///
/// This exists because that preamble is twelve lines, and twenty-seven copies of it is
/// twenty-seven chances to forget the INSUNITS guard and silently treat drawing units as
/// millimetres. <see cref="NewPayload"/> makes "echo the units on every response" structural
/// rather than remembered.
///
/// Deliberately NOT extended into a RunInTransaction(...) wrapper: each tool's
/// try/catch/Stopwatch/Log.Info block carries a different log line with different keys and
/// counts, and CLAUDE.md requires every log line to name its own operation with its own
/// metrics. A generic wrapper would either emit a generic line or need so many callbacks it
/// grows longer than what it replaces.
/// </summary>
internal readonly struct AcadContext
{
    private AcadContext(Document doc, Database db, int insUnitsCode, double mmPerDrawingUnit)
    {
        Doc = doc;
        Db = db;
        InsUnitsCode = insUnitsCode;
        MmPerDrawingUnit = mmPerDrawingUnit;
    }

    public Document Doc { get; }

    public Database Db { get; }

    public int InsUnitsCode { get; }

    public double MmPerDrawingUnit { get; }

    /// <summary>
    /// Opens the drawing the current tool call targets and resolves its unit factor. That is
    /// the document the gateway locked — which may not be the active one when the caller
    /// passed a "drawing" argument. Returns false with a ready-made failure when there is no
    /// drawing, or when INSUNITS is one this server refuses to guess at — notably 0
    /// (unitless), where assuming millimetres would make every tool wrong.
    /// </summary>
    public static bool TryOpen(out AcadContext context, out ToolResult? failure)
    {
        context = default;
        failure = null;

        // The fallback only matters for code running outside the gateway; every tool call
        // comes through it with CurrentDocument set.
        Document? doc = AcadDocumentGateway.CurrentDocument ?? Application.DocumentManager.MdiActiveDocument;
        if (doc is null)
        {
            failure = ToolResult.Fail("No drawing is open in AutoCAD.");
            return false;
        }

        Database db = doc.Database;

        int insUnitsCode = (int)db.Insunits;
        if (!Units.TryGetMmPerDrawingUnit(insUnitsCode, out double mmPerDrawingUnit))
        {
            failure = ToolResult.Fail(Units.DescribeUnsupportedUnits(insUnitsCode));
            return false;
        }

        context = new AcadContext(doc, db, insUnitsCode, mmPerDrawingUnit);
        return true;
    }

    /// <summary>
    /// A response payload pre-seeded with the context every database tool must echo: which
    /// drawing it acted on, and the units its numbers are denominated in. Naming the drawing
    /// is what lets a caller notice that a call landed somewhere other than it intended —
    /// handles are only unique within one drawing.
    /// </summary>
    public JsonObject NewPayload()
    {
        return new JsonObject
        {
            ["drawing"] = DrawingSelector.GetFileName(Doc.Name),
            ["drawing_path"] = Doc.Name,
            ["drawing_is_active"] = ReferenceEquals(Doc, Application.DocumentManager.MdiActiveDocument),
            ["mm_per_drawing_unit"] = MmPerDrawingUnit,
            ["insunits"] = InsUnitsCode,
        };
    }

    public double ToDu(double mm_Value) => Units.MmToDrawingUnits(mm_Value, MmPerDrawingUnit);

    public double ToMm(double du_Value) => Units.DrawingUnitsToMm(du_Value, MmPerDrawingUnit);

    public Point3d ToPoint3d(Mm_Point mm_Point)
    {
        return new Point3d(ToDu(mm_Point.Mm_X), ToDu(mm_Point.Mm_Y), ToDu(mm_Point.Mm_Z));
    }

    public Point2d ToPoint2d(double mm_X, double mm_Y)
    {
        return new Point2d(ToDu(mm_X), ToDu(mm_Y));
    }

    public Mm_Point ToMmPoint(Point3d du_Point)
    {
        return new Mm_Point(ToMm(du_Point.X), ToMm(du_Point.Y), ToMm(du_Point.Z));
    }

    /// <summary>Renders a database point as the [x, y, z] millimetre array used in payloads.</summary>
    public JsonArray ToMmArray(Point3d du_Point)
    {
        return new JsonArray
        {
            ToMm(du_Point.X),
            ToMm(du_Point.Y),
            ToMm(du_Point.Z),
        };
    }
}
