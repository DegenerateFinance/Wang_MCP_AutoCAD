using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// Handle syntax is checked before the database is touched, so these rules decide whether a
/// caller gets "handles are hexadecimal, use acad_list_entities" or an opaque AutoCAD error.
/// </summary>
public sealed class HandleSyntaxTests
{
    [Theory]
    [InlineData("1A7", "1A7")]
    [InlineData("1a7", "1A7")]
    [InlineData("2B", "2B")]
    [InlineData("FFFF", "FFFF")]
    [InlineData("  1a7  ", "1A7")]
    [InlineData("0x1A7", "1A7")]
    [InlineData("0X1a7", "1A7")]
    public void TryNormalize_AcceptsHexAndUppercases(string raw, string expected)
    {
        bool ok = HandleSyntax.TryNormalize(raw, out string normalized, out string error);

        Assert.True(ok, error);
        Assert.Equal(expected, normalized);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("12G4")]
    [InlineData("zzz")]
    [InlineData("1A7!")]
    [InlineData("1 A7")]
    [InlineData("-1A7")]
    public void TryNormalize_RejectsNonHex_AndSaysWhereToGetOne(string raw)
    {
        bool ok = HandleSyntax.TryNormalize(raw, out _, out string error);

        Assert.False(ok);
        Assert.Contains("hexadecimal", error);
        Assert.Contains("acad_list_entities", error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryNormalize_RejectsEmpty(string? raw)
    {
        bool ok = HandleSyntax.TryNormalize(raw, out _, out string error);

        Assert.False(ok);
        Assert.Contains("empty", error);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("000")]
    [InlineData("0x0")]
    public void TryNormalize_RejectsTheNullHandle(string raw)
    {
        bool ok = HandleSyntax.TryNormalize(raw, out _, out string error);

        Assert.False(ok);
        Assert.Contains("null handle", error);
    }

    [Fact]
    public void TryNormalize_RejectsOverlongInput()
    {
        bool ok = HandleSyntax.TryNormalize(new string('A', 17), out _, out string error);

        Assert.False(ok);
        Assert.Contains("too long", error);
    }

    [Fact]
    public void TryNormalize_AcceptsExactlySixteenDigits()
    {
        bool ok = HandleSyntax.TryNormalize(new string('A', 16), out string normalized, out string error);

        Assert.True(ok, error);
        Assert.Equal(new string('A', 16), normalized);
    }

    [Fact]
    public void IsWellFormed_AgreesWithTryNormalize()
    {
        Assert.True(HandleSyntax.IsWellFormed("1A7"));
        Assert.False(HandleSyntax.IsWellFormed("zzz"));
    }
}
