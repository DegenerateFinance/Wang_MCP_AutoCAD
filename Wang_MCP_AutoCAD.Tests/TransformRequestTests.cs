using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Every transform decision that does not need AutoCAD. If these pass, the Acad side has
/// nothing left to validate — it builds a Matrix3d and applies it.
/// </summary>
public sealed class TransformRequestTests
{
    private static readonly Mm_Point Origin = new(0, 0, 0);
    private static readonly Mm_Point TenTen = new(10, 10, 0);

    [Fact]
    public void TryBuildMove_KeepsBothPoints()
    {
        bool ok = TransformRequest.TryBuildMove(Origin, TenTen, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.NotNull(request);
        Assert.Equal(TransformKind.Move, request!.Kind);
        Assert.Equal(Origin, request.Mm_Base);
        Assert.Equal(TenTen, request.Mm_Secondary);
    }

    /// <summary>A zero-length move is a no-op, not an error — a computed displacement may be 0.</summary>
    [Fact]
    public void TryBuildMove_IdenticalPointsIsAllowed()
    {
        bool ok = TransformRequest.TryBuildMove(TenTen, TenTen, out _, out string error);

        Assert.True(ok, error);
    }

    [Fact]
    public void TryBuildRotate_ConvertsDegreesToRadians()
    {
        bool ok = TransformRequest.TryBuildRotate(Origin, 90, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.Equal(Math.PI / 2, request!.Rad_Angle, precision: 12);
    }

    [Theory]
    [InlineData(-45.0)]
    [InlineData(720.0)]
    [InlineData(0.0)]
    public void TryBuildRotate_AcceptsNegativeAndOversizedAngles(double deg_Angle)
    {
        bool ok = TransformRequest.TryBuildRotate(Origin, deg_Angle, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.Equal(Units.DegToRad(deg_Angle), request!.Rad_Angle, precision: 12);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void TryBuildRotate_RejectsNonFiniteAngle(double deg_Angle)
    {
        bool ok = TransformRequest.TryBuildRotate(Origin, deg_Angle, out _, out string error);

        Assert.False(ok);
        Assert.Contains("deg_angle", error);
        Assert.Contains("finite", error);
    }

    [Fact]
    public void TryBuildScale_ZeroIsRejectedWithAnExplanation()
    {
        bool ok = TransformRequest.TryBuildScale(Origin, 0, out _, out string error);

        Assert.False(ok);
        Assert.Contains("factor", error);
        Assert.Contains("collapses", error);
    }

    /// <summary>A negative scale factor is a legal mirror-scale, so it must not be rejected.</summary>
    [Fact]
    public void TryBuildScale_NegativeIsAllowed()
    {
        bool ok = TransformRequest.TryBuildScale(Origin, -2, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.Equal(-2, request!.Factor);
    }

    [Fact]
    public void TryBuildScale_KeepsTheBasePoint()
    {
        bool ok = TransformRequest.TryBuildScale(TenTen, 2, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.Equal(TenTen, request!.Mm_Base);
        Assert.Equal(TransformKind.Scale, request.Kind);
    }

    [Fact]
    public void TryBuildMirror_IdenticalPointsIsRejected()
    {
        bool ok = TransformRequest.TryBuildMirror(TenTen, TenTen, out _, out string error);

        Assert.False(ok);
        Assert.Contains("identical", error);
        Assert.Contains("define a line", error);
    }

    [Fact]
    public void TryBuildMirror_KeepsBothEnds()
    {
        bool ok = TransformRequest.TryBuildMirror(Origin, TenTen, out TransformRequest? request, out string error);

        Assert.True(ok, error);
        Assert.Equal(TransformKind.Mirror, request!.Kind);
        Assert.Equal(Origin, request.Mm_Base);
        Assert.Equal(TenTen, request.Mm_Secondary);
    }

    [Fact]
    public void AllBuilders_RejectNonFinitePoints()
    {
        Mm_Point bad = new(double.NaN, 0, 0);

        Assert.False(TransformRequest.TryBuildMove(bad, TenTen, out _, out _));
        Assert.False(TransformRequest.TryBuildRotate(bad, 90, out _, out _));
        Assert.False(TransformRequest.TryBuildScale(bad, 2, out _, out _));
        Assert.False(TransformRequest.TryBuildMirror(bad, TenTen, out _, out _));
    }

    [Fact]
    public void ModeNames_MatchTheDocumentedSurface()
    {
        Assert.Equal(new[] { "move", "rotate", "scale", "mirror" }, TransformRequest.ModeNames);
    }
}
