using System.Text.Json;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public class PointArgsTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    // ---- TryReadPointMm -----------------------------------------------------------------

    [Fact]
    public void TryReadPointMm_ReadsTheTriple()
    {
        ToolArgs args = Parse("""{"mm_center_x":10,"mm_center_y":20,"mm_center_z":30}""");

        bool ok = PointArgs.TryReadPointMm(args, "mm_center", required: true, out Mm_Point point, out bool present, out string error);

        Assert.True(ok, error);
        Assert.True(present);
        Assert.Equal(new Mm_Point(10, 20, 30), point);
    }

    [Fact]
    public void TryReadPointMm_ZDefaultsToZero()
    {
        ToolArgs args = Parse("""{"mm_start_x":1,"mm_start_y":2}""");

        bool ok = PointArgs.TryReadPointMm(args, "mm_start", required: true, out Mm_Point point, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(0, point.Mm_Z);
    }

    [Fact]
    public void TryReadPointMm_MissingWhenRequired_NamesTheSuffixedFields()
    {
        ToolArgs args = Parse("{}");

        bool ok = PointArgs.TryReadPointMm(args, "mm_center", required: true, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("mm_center_x", error);
        Assert.Contains("mm_center_y", error);
    }

    [Fact]
    public void TryReadPointMm_AbsentWhenOptional_ReportsNotPresent()
    {
        ToolArgs args = Parse("{}");

        bool ok = PointArgs.TryReadPointMm(args, "mm_base", required: false, out _, out bool present, out string error);

        Assert.True(ok, error);
        Assert.False(present);
    }

    /// <summary>
    /// Half a point is always a mistake, even when the point itself is optional — silently
    /// defaulting the missing ordinate to 0 would place geometry somewhere unintended.
    /// </summary>
    [Theory]
    [InlineData("""{"mm_base_x":5}""", "mm_base_y")]
    [InlineData("""{"mm_base_y":5}""", "mm_base_x")]
    public void TryReadPointMm_HalfSuppliedPoint_IsRejected(string json, string missingField)
    {
        ToolArgs args = Parse(json);

        bool ok = PointArgs.TryReadPointMm(args, "mm_base", required: false, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains(missingField, error);
    }

    [Fact]
    public void TryReadPointMm_WrongOrdinateType_IsRejected()
    {
        ToolArgs args = Parse("""{"mm_center_x":"ten","mm_center_y":20}""");

        bool ok = PointArgs.TryReadPointMm(args, "mm_center", required: true, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("mm_center_x", error);
    }

    // ---- TryReadVertexListMm ------------------------------------------------------------

    [Fact]
    public void TryReadVertexListMm_ReadsVertices()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0},{"mm_x":10,"mm_y":0}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out IReadOnlyList<Mm_Vertex> vertices, out string error);

        Assert.True(ok, error);
        Assert.Equal(2, vertices.Count);
        Assert.Equal(new Mm_Vertex(0, 0, 0), vertices[0]);
        Assert.Equal(new Mm_Vertex(10, 0, 0), vertices[1]);
    }

    [Fact]
    public void TryReadVertexListMm_BulgeIsOptionalAndDefaultsToZero()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0,"bulge":1},{"mm_x":10,"mm_y":0}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out IReadOnlyList<Mm_Vertex> vertices, out string error);

        Assert.True(ok, error);
        Assert.Equal(1, vertices[0].Bulge);
        Assert.Equal(0, vertices[1].Bulge);
    }

    [Fact]
    public void TryReadVertexListMm_BelowMinimumCount_IsRejected()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("between 2 and 5000", error);
        Assert.Contains("contained 1", error);
    }

    [Fact]
    public void TryReadVertexListMm_NonObjectElement_NamesTheIndex()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0},[10,0]]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
        Assert.Contains("an array", error);
    }

    [Fact]
    public void TryReadVertexListMm_MissingKey_NamesIndexAndKey()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0},{"mm_x":10}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
        Assert.Contains("mm_y", error);
    }

    /// <summary>
    /// A lightweight polyline is 2D. Silently ignoring a per-vertex Z is how a caller ends up
    /// believing it drew 3D geometry, so it is rejected with a pointer to mm_elevation.
    /// </summary>
    [Fact]
    public void TryReadVertexListMm_PerVertexZ_IsRejectedNotIgnored()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0},{"mm_x":10,"mm_y":0,"mm_z":5}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
        Assert.Contains("mm_elevation", error);
    }

    [Fact]
    public void TryReadVertexListMm_NonNumericOrdinate_NamesIndexAndKey()
    {
        ToolArgs args = Parse("""{"mm_vertices":[{"mm_x":0,"mm_y":0},{"mm_x":"ten","mm_y":0}]}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("item 1", error);
        Assert.Contains("mm_x", error);
        Assert.Contains("a string", error);
    }

    [Fact]
    public void TryReadVertexListMm_NotAnArray_IsRejected()
    {
        ToolArgs args = Parse("""{"mm_vertices":{"mm_x":0}}""");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("an object", error);
    }

    [Fact]
    public void TryReadVertexListMm_Missing_IsRejected()
    {
        ToolArgs args = Parse("{}");

        bool ok = PointArgs.TryReadVertexListMm(args, "mm_vertices", 2, 5000, out _, out string error);

        Assert.False(ok);
        Assert.Contains("missing", error);
    }
}
