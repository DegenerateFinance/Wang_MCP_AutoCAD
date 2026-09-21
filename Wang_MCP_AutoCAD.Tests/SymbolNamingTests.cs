using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// These rules mirror AutoCAD's own SymbolUtilityServices.ValidateSymbolName, which throws
/// rather than returning a bool and cannot be reached from a test. Checking them here first
/// is what turns an eInvalidSymbolTableName escaping a transaction into a message naming the
/// offending character.
/// </summary>
public sealed class SymbolNamingTests
{
    [Theory]
    [InlineData("WALLS")]
    [InlineData("A-WALL-FULL")]
    [InlineData("0")]
    [InlineData("layer with spaces")]
    [InlineData("Ünïcøde")]
    [InlineData("dots.and-dashes_1")]
    public void TryValidate_AcceptsOrdinaryNames(string name)
    {
        Assert.True(LayerNaming.TryValidate(name, out string error), error);
    }

    [Theory]
    [InlineData("a<b", '<')]
    [InlineData("a>b", '>')]
    [InlineData("a/b", '/')]
    [InlineData("a\\b", '\\')]
    [InlineData("a\"b", '"')]
    [InlineData("a:b", ':')]
    [InlineData("a;b", ';')]
    [InlineData("a?b", '?')]
    [InlineData("a*b", '*')]
    [InlineData("a|b", '|')]
    [InlineData("a,b", ',')]
    [InlineData("a=b", '=')]
    [InlineData("a`b", '`')]
    public void TryValidate_RejectsForbiddenCharactersAndNamesTheOffender(string name, char offender)
    {
        bool ok = LayerNaming.TryValidate(name, out string error);

        Assert.False(ok);
        Assert.Contains(offender.ToString(), error);
        Assert.Contains(name, error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryValidate_RejectsBlank(string? name)
    {
        bool ok = LayerNaming.TryValidate(name, out string error);

        Assert.False(ok);
        Assert.Contains("blank", error);
    }

    [Fact]
    public void TryValidate_RejectsOverlongNames()
    {
        bool ok = LayerNaming.TryValidate(new string('A', SymbolNaming.MaxLength + 1), out string error);

        Assert.False(ok);
        Assert.Contains("maximum", error);
    }

    [Fact]
    public void TryValidate_AcceptsExactlyMaxLength()
    {
        Assert.True(LayerNaming.TryValidate(new string('A', SymbolNaming.MaxLength), out _));
    }

    [Fact]
    public void TryValidate_NamesTheKindBeingValidated()
    {
        LayerNaming.TryValidate("", out string layerError);
        BlockNaming.TryValidate("", out string blockError);

        Assert.Contains("layer", layerError);
        Assert.Contains("block", blockError);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("Defpoints", true)]
    [InlineData("DEFPOINTS", true)]
    [InlineData("defpoints", true)]
    [InlineData("WALLS", false)]
    [InlineData("00", false)]
    public void IsReserved_RecognisesTheProtectedLayers(string name, bool expected)
    {
        Assert.Equal(expected, LayerNaming.IsReserved(name));
    }

    /// <summary>
    /// A leading asterisk marks an anonymous block — *U dynamic variants, *D dimension
    /// blocks, *Model_Space. Those are AutoCAD's to create, never a caller's.
    /// </summary>
    [Theory]
    [InlineData("*U3")]
    [InlineData("*Model_Space")]
    [InlineData("*D1")]
    public void BlockNaming_RejectsAnonymousNames(string name)
    {
        bool ok = BlockNaming.TryValidate(name, out string error);

        Assert.False(ok);
        Assert.Contains("anonymous", error);
    }

    [Fact]
    public void BlockNaming_AcceptsOrdinaryBlockNames()
    {
        Assert.True(BlockNaming.TryValidate("DOOR-900", out string error), error);
    }

    [Theory]
    [InlineData("*U3", true)]
    [InlineData("*Model_Space", true)]
    [InlineData("DOOR", false)]
    public void IsAnonymousOrLayout_DetectsTheAsteriskPrefix(string name, bool expected)
    {
        Assert.Equal(expected, BlockNaming.IsAnonymousOrLayout(name));
    }
}
