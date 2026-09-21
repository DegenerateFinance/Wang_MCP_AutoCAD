namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// A point in millimetres, as it arrives on the wire. Autodesk-free on purpose so it can
/// cross the protocol/AutoCAD boundary freely — Point3d construction stays on the Acad side,
/// which is also where the mm-to-drawing-unit conversion happens.
/// </summary>
public readonly record struct Mm_Point(double Mm_X, double Mm_Y, double Mm_Z)
{
    public static Mm_Point Origin => new(0, 0, 0);
}

/// <summary>
/// A polyline vertex: a 2D point plus its bulge. Bulge is tan(theta / 4) for the included
/// angle of the arc segment that follows — dimensionless, so it is never unit-converted.
/// </summary>
public readonly record struct Mm_Vertex(double Mm_X, double Mm_Y, double Bulge);
