namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Drawing-unit conversion, driven by the drawing's INSUNITS system variable.
///
/// This is deliberately Autodesk-free and takes a plain int code rather than a UnitsValue:
/// that int hop is what keeps the conversion table unit-testable, since the test project
/// never gets the AutoCAD DLLs. The Acad side casts UnitsValue to int and calls in here.
///
/// Geometry read from the database is in drawing units and is NOT implicitly millimetres —
/// hence the Du_/Mm_ naming split everywhere downstream.
/// </summary>
public static class Units
{
    /// <summary>
    /// Millimetres per drawing unit for an INSUNITS code. Returns false for codes this
    /// server refuses to guess at — notably 0 (Unitless), where silently assuming
    /// millimetres would make every drawing tool quietly wrong.
    /// </summary>
    public static bool TryGetMmPerDrawingUnit(int insUnitsCode, out double mmPerDrawingUnit)
    {
        switch (insUnitsCode)
        {
            // 0 = Unitless, handled by the default arm: refused, never guessed at.
            case 1: mmPerDrawingUnit = 25.4; return true;           // Inches
            case 2: mmPerDrawingUnit = 304.8; return true;          // Feet
            case 3: mmPerDrawingUnit = 1609344.0; return true;      // Miles
            case 4: mmPerDrawingUnit = 1.0; return true;            // Millimeters
            case 5: mmPerDrawingUnit = 10.0; return true;           // Centimeters
            case 6: mmPerDrawingUnit = 1000.0; return true;         // Meters
            case 7: mmPerDrawingUnit = 1000000.0; return true;      // Kilometers
            case 8: mmPerDrawingUnit = 0.0000254; return true;      // Microinches
            case 9: mmPerDrawingUnit = 0.0254; return true;         // Mils (thousandths of an inch)
            case 10: mmPerDrawingUnit = 914.4; return true;         // Yards
            case 11: mmPerDrawingUnit = 0.0000001; return true;     // Angstroms
            case 12: mmPerDrawingUnit = 0.000001; return true;      // Nanometers
            case 13: mmPerDrawingUnit = 0.001; return true;         // Microns
            case 14: mmPerDrawingUnit = 100.0; return true;         // Decimeters
            case 15: mmPerDrawingUnit = 10000.0; return true;       // Decameters
            case 16: mmPerDrawingUnit = 100000.0; return true;      // Hectometers
            case 17: mmPerDrawingUnit = 1000000000000.0; return true; // Gigameters

            // 18 (astronomical units), 19 (light years) and 20 (parsecs) are refused:
            // a drawing at that scale is not one this server has any business editing in
            // millimetres, and the factors are large enough to lose all precision.
            default:
                mmPerDrawingUnit = 0;
                return false;
        }
    }

    /// <summary>The message handed back to the client when INSUNITS cannot be converted.</summary>
    public static string DescribeUnsupportedUnits(int insUnitsCode)
    {
        if (insUnitsCode == 0)
        {
            return "This drawing's INSUNITS is 0 (unitless), so millimetre arguments cannot be "
                 + "converted to drawing units. Set INSUNITS in AutoCAD (for example to 4 for "
                 + "millimetres) and retry.";
        }

        return $"This drawing's INSUNITS is {insUnitsCode}, which this server does not know how "
             + "to convert to millimetres. Set INSUNITS to a length unit such as 4 (millimetres) "
             + "and retry.";
    }

    public static double MmToDrawingUnits(double mm_Value, double mmPerDrawingUnit) => mm_Value / mmPerDrawingUnit;

    public static double DrawingUnitsToMm(double du_Value, double mmPerDrawingUnit) => du_Value * mmPerDrawingUnit;

    /// <summary>
    /// LineWeight is stored in hundredths of a millimetre and is independent of INSUNITS.
    /// Running it through the drawing-unit factor is a classic and silent bug.
    /// </summary>
    public static double LineWeightToMm(int lineWeightEnumValue) => lineWeightEnumValue / 100.0;
}
