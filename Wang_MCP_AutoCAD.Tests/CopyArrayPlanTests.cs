using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class CopyArrayPlanTests
{
    private static readonly Mm_Point Step = new(100, 0, 0);

    [Fact]
    public void SingleCopy_IsOneDisplacement()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: Step,
            count: 1,
            handleCount: 5,
            maxNewEntities: 1000,
            mm_Offsets: out IReadOnlyList<Mm_Point> offsets,
            error: out string error);

        Assert.True(ok, error);
        Assert.Equal(new[] { Step }, offsets);
    }

    /// <summary>Copy N sits at N times the displacement — cumulative, not repeated.</summary>
    [Fact]
    public void Offsets_AreCumulativeMultiples()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: new Mm_Point(10, -5, 2),
            count: 3,
            handleCount: 1,
            maxNewEntities: 1000,
            mm_Offsets: out IReadOnlyList<Mm_Point> offsets,
            error: out string error);

        Assert.True(ok, error);
        Assert.Equal(
            new[] { new Mm_Point(10, -5, 2), new Mm_Point(20, -10, 4), new Mm_Point(30, -15, 6) },
            offsets);
    }

    [Fact]
    public void ZeroDisplacement_IsRejected()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: Mm_Point.Origin,
            count: 1,
            handleCount: 1,
            maxNewEntities: 1000,
            mm_Offsets: out _,
            error: out string error);

        Assert.False(ok);
        Assert.Contains("zero", error);
        Assert.Contains("on top", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(CopyArrayPlan.MaxCount + 1)]
    public void CountOutOfRange_IsRejected(int count)
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: Step,
            count: count,
            handleCount: 1,
            maxNewEntities: 100000,
            mm_Offsets: out _,
            error: out string error);

        Assert.False(ok);
        Assert.Contains("count", error);
    }

    [Fact]
    public void TotalOverTheCap_IsRejectedWithTheArithmetic()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: Step,
            count: 11,
            handleCount: 100,
            maxNewEntities: 1000,
            mm_Offsets: out _,
            error: out string error);

        Assert.False(ok);
        Assert.Contains("1100", error);
        Assert.Contains("1000", error);
    }

    [Fact]
    public void TotalExactlyAtTheCap_IsAllowed()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: Step,
            count: 10,
            handleCount: 100,
            maxNewEntities: 1000,
            mm_Offsets: out IReadOnlyList<Mm_Point> offsets,
            error: out string error);

        Assert.True(ok, error);
        Assert.Equal(10, offsets.Count);
    }

    [Fact]
    public void NonFiniteDisplacement_IsRejected()
    {
        bool ok = CopyArrayPlan.TryBuild(
            mm_Displacement: new Mm_Point(double.NaN, 0, 0),
            count: 1,
            handleCount: 1,
            maxNewEntities: 1000,
            mm_Offsets: out _,
            error: out string error);

        Assert.False(ok);
        Assert.Contains("finite", error);
    }
}
