using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

/// <summary>
/// The integer values asserted here are AutoCAD's AttachmentPoint members, read off
/// acdbmgd.dll metadata (assembly 25.1.0.0) rather than copied from documentation. They are
/// spelled as literals on purpose: the test project never gets the AutoCAD DLLs, so this file
/// is the only place the wire-name-to-enum contract can be pinned down, and a literal that
/// disagrees with the enum is exactly the bug worth catching.
/// </summary>
public sealed class TextJustificationTests
{
    [Theory]
    [InlineData("left", 10)]
    [InlineData("center", 11)]
    [InlineData("right", 12)]
    [InlineData("top_left", 1)]
    [InlineData("top_center", 2)]
    [InlineData("top_right", 3)]
    [InlineData("middle_left", 4)]
    [InlineData("middle_center", 5)]
    [InlineData("middle_right", 6)]
    [InlineData("bottom_left", 7)]
    [InlineData("bottom_center", 8)]
    [InlineData("bottom_right", 9)]
    public void TryGetAttachmentPoint_MapsEveryAdvertisedName(string name, int expected)
    {
        Assert.True(TextJustification.TryGetAttachmentPoint(name, out int actual, out string error));
        Assert.Equal(expected, actual);
        Assert.Equal("", error);
    }

    /// <summary>
    /// Every name the schema advertises must resolve. Without this, adding a value to Allowed
    /// and forgetting the switch arm produces a tool that offers an option it then rejects.
    /// </summary>
    [Fact]
    public void TryGetAttachmentPoint_ResolvesEveryAllowedValue()
    {
        Assert.NotEmpty(TextJustification.Allowed);

        foreach (string name in TextJustification.Allowed)
        {
            Assert.True(
                TextJustification.TryGetAttachmentPoint(name, out int value, out string error),
                $"Allowed justification \"{name}\" did not resolve: {error}");

            Assert.InRange(value, 1, 24);
        }
    }

    /// <summary>The advertised names must round-trip, or a creation payload reports a different
    /// justification than the caller asked for.</summary>
    [Fact]
    public void DescribeAttachmentPoint_RoundTripsEveryAllowedValue()
    {
        foreach (string name in TextJustification.Allowed)
        {
            Assert.True(TextJustification.TryGetAttachmentPoint(name, out int value, out _));
            Assert.Equal(name, TextJustification.DescribeAttachmentPoint(value));
        }
    }

    [Theory]
    [InlineData("LEFT", 10)]
    [InlineData("Middle_Center", 5)]
    [InlineData("  top_right  ", 3)]
    public void TryGetAttachmentPoint_IsCaseInsensitiveAndTrims(string name, int expected)
    {
        Assert.True(TextJustification.TryGetAttachmentPoint(name, out int actual, out _));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void TryGetAttachmentPoint_RejectsMissingName(string? name)
    {
        Assert.False(TextJustification.TryGetAttachmentPoint(name, out _, out string error));
        Assert.NotEqual("", error);
    }

    /// <summary>
    /// Align and Fit take a second point and Mid duplicates Center; none is advertised, so
    /// none may be accepted by name either.
    /// </summary>
    [Theory]
    [InlineData("base_fit")]
    [InlineData("base_align")]
    [InlineData("middle_mid")]
    [InlineData("centre")]
    [InlineData("TopLeft")]
    public void TryGetAttachmentPoint_RejectsUnadvertisedNames(string name)
    {
        Assert.False(TextJustification.TryGetAttachmentPoint(name, out _, out string error));
        Assert.Contains(name, error);
    }

    /// <summary>
    /// A read must never fail on text someone else drew, even when its justification is one
    /// this surface does not offer.
    /// </summary>
    [Theory]
    [InlineData(13, "base_align")]
    [InlineData(17, "base_fit")]
    [InlineData(23, "middle_mid")]
    public void DescribeAttachmentPoint_NamesValuesItDoesNotAccept(int value, string expected)
    {
        Assert.Equal(expected, TextJustification.DescribeAttachmentPoint(value));
    }

    [Fact]
    public void DescribeAttachmentPoint_DoesNotThrowOnAnUnknownValue()
    {
        Assert.Equal("unknown_99", TextJustification.DescribeAttachmentPoint(99));
    }

    /// <summary>
    /// The AlignmentPoint trap, pinned as a rule: ONLY baseline-left leaves Position
    /// authoritative. If this ever reports true for another justification, text written with
    /// only Position set would silently land at the wrong place.
    /// </summary>
    [Fact]
    public void UsesPositionOnly_IsTrueForBaselineLeftAlone()
    {
        Assert.True(TextJustification.TryGetAttachmentPoint("left", out int baseLeft, out _));
        Assert.True(TextJustification.UsesPositionOnly(baseLeft));

        foreach (string name in TextJustification.Allowed)
        {
            if (name == "left")
            {
                continue;
            }

            Assert.True(TextJustification.TryGetAttachmentPoint(name, out int value, out _));
            Assert.False(
                TextJustification.UsesPositionOnly(value),
                $"\"{name}\" must be treated as alignment-point driven, not position-driven.");
        }
    }

    /// <summary>
    /// The two defaults differ deliberately — TEXT defaults to its baseline like the TEXT
    /// command, MTEXT to its top-left corner like MTEXT — so a refactor that collapses them
    /// into one constant should fail here.
    /// </summary>
    [Fact]
    public void Defaults_AreAdvertisedAndDistinct()
    {
        Assert.Contains(TextJustification.DefaultJustification, TextJustification.Allowed);
        Assert.Contains(TextJustification.DefaultAttachment, TextJustification.Allowed);
        Assert.NotEqual(TextJustification.DefaultJustification, TextJustification.DefaultAttachment);

        Assert.True(TextJustification.TryGetAttachmentPoint(
            TextJustification.DefaultJustification,
            out int textDefault,
            out _));

        // A new TEXT entity must come out baseline-left, which is AutoCAD's own default.
        Assert.Equal(10, textDefault);

        Assert.True(TextJustification.TryGetAttachmentPoint(
            TextJustification.DefaultAttachment,
            out int mTextDefault,
            out _));

        Assert.Equal(1, mTextDefault);
    }

    [Fact]
    public void Allowed_ContainsNoDuplicatesAndIsLowercase()
    {
        Assert.Equal(TextJustification.Allowed.Count, TextJustification.Allowed.Distinct().Count());

        foreach (string name in TextJustification.Allowed)
        {
            Assert.Equal(name.ToLowerInvariant(), name);
        }
    }

    /// <summary>
    /// Distinct names must not collapse onto the same enum value, which would make two
    /// advertised options silently synonymous.
    /// </summary>
    [Fact]
    public void Allowed_MapsToDistinctAttachmentPoints()
    {
        HashSet<int> seen = new();

        foreach (string name in TextJustification.Allowed)
        {
            Assert.True(TextJustification.TryGetAttachmentPoint(name, out int value, out _));
            Assert.True(seen.Add(value), $"\"{name}\" duplicates attachment point {value}.");
        }
    }
}
