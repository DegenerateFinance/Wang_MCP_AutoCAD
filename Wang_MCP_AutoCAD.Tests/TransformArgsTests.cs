using System.Text.Json;
using System.Text.Json.Nodes;
using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// acad_transform's argument reading. The behaviour that matters most is the foreign-argument
/// guard: a caller sending a mode's neighbour's argument must be told, not silently ignored.
/// </summary>
public sealed class TransformArgsTests
{
    private static ToolArgs Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return new ToolArgs(document.RootElement.Clone());
    }

    [Fact]
    public void Move_ReadsBothPoints()
    {
        ToolArgs args = Parse("""{"mode":"move","mm_from_x":1,"mm_from_y":2,"mm_to_x":11,"mm_to_y":22,"mm_to_z":5}""");

        bool ok = TransformArgs.TryRead(args, out TransformRequest? request, out string mode, out string error);

        Assert.True(ok, error);
        Assert.Equal("move", mode);
        Assert.Equal(TransformKind.Move, request!.Kind);
        Assert.Equal(new Mm_Point(1, 2, 0), request.Mm_Base);
        Assert.Equal(new Mm_Point(11, 22, 5), request.Mm_Secondary);
    }

    [Fact]
    public void Rotate_ConvertsDegrees()
    {
        ToolArgs args = Parse("""{"mode":"rotate","mm_base_x":5,"mm_base_y":5,"deg_angle":90}""");

        bool ok = TransformArgs.TryRead(args, out TransformRequest? request, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(TransformKind.Rotate, request!.Kind);
        Assert.Equal(new Mm_Point(5, 5, 0), request.Mm_Base);
        Assert.Equal(Math.PI / 2, request.Rad_Angle, precision: 12);
    }

    [Fact]
    public void Scale_ReadsFactor()
    {
        ToolArgs args = Parse("""{"mode":"scale","mm_base_x":0,"mm_base_y":0,"factor":2.5}""");

        bool ok = TransformArgs.TryRead(args, out TransformRequest? request, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(2.5, request!.Factor);
    }

    [Fact]
    public void Mirror_ReadsTheLine()
    {
        ToolArgs args = Parse("""{"mode":"mirror","mm_line_start_x":0,"mm_line_start_y":0,"mm_line_end_x":0,"mm_line_end_y":10}""");

        bool ok = TransformArgs.TryRead(args, out TransformRequest? request, out _, out string error);

        Assert.True(ok, error);
        Assert.Equal(TransformKind.Mirror, request!.Kind);
        Assert.Equal(new Mm_Point(0, 10, 0), request.Mm_Secondary);
    }

    [Fact]
    public void Mode_IsCaseInsensitiveAndCanonicalised()
    {
        ToolArgs args = Parse("""{"mode":"ROTATE","mm_base_x":0,"mm_base_y":0,"deg_angle":45}""");

        bool ok = TransformArgs.TryRead(args, out _, out string mode, out string error);

        Assert.True(ok, error);
        Assert.Equal("rotate", mode);
    }

    [Fact]
    public void MissingMode_ListsTheModes()
    {
        ToolArgs args = Parse("""{"mm_from_x":0,"mm_from_y":0}""");

        bool ok = TransformArgs.TryRead(args, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("mode", error);
        Assert.Contains("move, rotate, scale, mirror", error);
    }

    [Fact]
    public void UnknownMode_IsRejected()
    {
        ToolArgs args = Parse("""{"mode":"stretch"}""");

        Assert.False(TransformArgs.TryRead(args, out _, out _, out string error));
        Assert.Contains("stretch", error);
    }

    /// <summary>The headline case: deg_angle sent with scale must not be silently dropped.</summary>
    [Fact]
    public void ForeignScalar_IsRejectedAndNamed()
    {
        ToolArgs args = Parse("""{"mode":"scale","mm_base_x":0,"mm_base_y":0,"factor":2,"deg_angle":30}""");

        bool ok = TransformArgs.TryRead(args, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("\"deg_angle\"", error);
        Assert.Contains("scale", error);
        Assert.Contains("factor", error);
    }

    [Fact]
    public void ForeignPoint_IsRejectedEvenIfOnlyZIsSent()
    {
        ToolArgs args = Parse("""{"mode":"rotate","mm_base_x":0,"mm_base_y":0,"deg_angle":30,"mm_to_z":1}""");

        bool ok = TransformArgs.TryRead(args, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("\"mm_to_z\"", error);
    }

    [Fact]
    public void ForeignArguments_AreAllNamedAtOnce()
    {
        ToolArgs args = Parse("""{"mode":"move","mm_from_x":0,"mm_from_y":0,"mm_to_x":1,"mm_to_y":1,"factor":2,"mm_base_x":0}""");

        bool ok = TransformArgs.TryRead(args, out _, out _, out string error);

        Assert.False(ok);
        Assert.Contains("\"factor\"", error);
        Assert.Contains("\"mm_base_x\"", error);
    }

    /// <summary>mm_base is shared by rotate and scale, so it must not count as foreign to either.</summary>
    [Fact]
    public void SharedBasePoint_IsNotForeignToScaleOrRotate()
    {
        Assert.True(TransformArgs.TryRead(Parse("""{"mode":"scale","mm_base_x":1,"mm_base_y":1,"factor":3}"""), out _, out _, out string e1), e1);
        Assert.True(TransformArgs.TryRead(Parse("""{"mode":"rotate","mm_base_x":1,"mm_base_y":1,"deg_angle":3}"""), out _, out _, out string e2), e2);
    }

    [Fact]
    public void Move_MissingDestinationIsRejected()
    {
        ToolArgs args = Parse("""{"mode":"move","mm_from_x":0,"mm_from_y":0}""");

        Assert.False(TransformArgs.TryRead(args, out _, out _, out string error));
        Assert.Contains("mm_to_x", error);
    }

    [Fact]
    public void Rotate_MissingAngleIsRejected()
    {
        ToolArgs args = Parse("""{"mode":"rotate","mm_base_x":0,"mm_base_y":0}""");

        Assert.False(TransformArgs.TryRead(args, out _, out _, out string error));
        Assert.Contains("deg_angle", error);
    }

    [Fact]
    public void Scale_ZeroFactorIsRejected()
    {
        ToolArgs args = Parse("""{"mode":"scale","mm_base_x":0,"mm_base_y":0,"factor":0}""");

        Assert.False(TransformArgs.TryRead(args, out _, out _, out string error));
        Assert.Contains("factor", error);
    }

    [Fact]
    public void Mirror_DegenerateLineIsRejected()
    {
        ToolArgs args = Parse("""{"mode":"mirror","mm_line_start_x":3,"mm_line_start_y":3,"mm_line_end_x":3,"mm_line_end_y":3}""");

        Assert.False(TransformArgs.TryRead(args, out _, out _, out string error));
        Assert.Contains("identical", error);
    }

    [Fact]
    public void Schema_AdvertisesEveryArgumentTheReaderConsumes()
    {
        JsonObject schema = TransformArgs.AddTo(ToolSchema.Object()).Build();
        JsonObject properties = schema["properties"]!.AsObject();

        string[] expected =
        {
            "mode",
            "mm_from_x", "mm_from_y", "mm_from_z",
            "mm_to_x", "mm_to_y", "mm_to_z",
            "mm_base_x", "mm_base_y", "mm_base_z",
            "deg_angle", "factor",
            "mm_line_start_x", "mm_line_start_y", "mm_line_start_z",
            "mm_line_end_x", "mm_line_end_y", "mm_line_end_z",
        };

        foreach (string name in expected)
        {
            Assert.True(properties.ContainsKey(name), $"schema is missing \"{name}\"");
        }

        Assert.Equal(expected.Length, properties.Count);
    }

    /// <summary>Only "mode" can be schema-required: every other argument depends on the mode.</summary>
    [Fact]
    public void Schema_RequiresOnlyMode()
    {
        JsonObject schema = TransformArgs.AddTo(ToolSchema.Object()).Build();
        JsonArray required = schema["required"]!.AsArray();

        Assert.Single(required);
        Assert.Equal("mode", required[0]!.GetValue<string>());
    }

    // ---- IsReflection -------------------------------------------------------------------

    [Fact]
    public void IsReflection_TrueForMirror()
    {
        TransformRequest.TryBuildMirror(new Mm_Point(0, 0, 0), new Mm_Point(0, 1, 0), out TransformRequest? request, out _);

        Assert.True(request!.IsReflection);
    }

    [Fact]
    public void IsReflection_TrueForNegativeScale()
    {
        TransformRequest.TryBuildScale(Mm_Point.Origin, -1, out TransformRequest? request, out _);

        Assert.True(request!.IsReflection);
    }

    [Fact]
    public void IsReflection_FalseForPositiveScaleRotateAndMove()
    {
        TransformRequest.TryBuildScale(Mm_Point.Origin, 2, out TransformRequest? scale, out _);
        TransformRequest.TryBuildRotate(Mm_Point.Origin, 180, out TransformRequest? rotate, out _);
        TransformRequest.TryBuildMove(Mm_Point.Origin, new Mm_Point(1, 1, 0), out TransformRequest? move, out _);

        Assert.False(scale!.IsReflection);
        Assert.False(rotate!.IsReflection);
        Assert.False(move!.IsReflection);
    }
}
