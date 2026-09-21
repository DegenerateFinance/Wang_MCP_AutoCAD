using Wang_MCP_AutoCAD.Mcp;
using Xunit;

namespace Wang_MCP_AutoCAD.Tests;

public sealed class MTextEscaperTests
{
    [Fact]
    public void Escape_DoublesBackslashes()
    {
        Assert.Equal("a\\\\b", MTextEscaper.Escape("a\\b"));
    }

    [Fact]
    public void Escape_EscapesBraces()
    {
        Assert.Equal("\\{a\\}", MTextEscaper.Escape("{a}"));
    }

    [Fact]
    public void Escape_ConvertsNewlineToParagraphCode()
    {
        Assert.Equal("a\\Pb", MTextEscaper.Escape("a\nb"));
    }

    [Fact]
    public void Escape_ConvertsCrLfToASingleParagraphCode()
    {
        Assert.Equal("a\\Pb", MTextEscaper.Escape("a\r\nb"));
    }

    [Fact]
    public void Escape_LeavesPlainTextAlone()
    {
        string plain = "Section A-A, scale 1:50";

        Assert.Equal(plain, MTextEscaper.Escape(plain));
    }

    /// <summary>
    /// The motivating case: a Windows path pasted into a note. Unescaped, MText reads the
    /// backslashes as formatting codes and the text silently corrupts.
    /// </summary>
    [Fact]
    public void Escape_WindowsPathSurvivesRoundTrip()
    {
        string path = @"C:\Users\drawings\plan.dwg";

        string escaped = MTextEscaper.Escape(path);

        // Every backslash must be doubled, so no single backslash remains to be read as the
        // start of a formatting code such as \U. Three backslashes in, six out.
        Assert.Equal(@"C:\\Users\\drawings\\plan.dwg", escaped);
        Assert.Equal(path, MTextEscaper.Unescape(escaped));
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("a\\b")]
    [InlineData("{group}")]
    [InlineData("line1\nline2")]
    [InlineData(@"C:\temp\{x}")]
    public void Escape_ThenUnescape_IsIdentity(string original)
    {
        Assert.Equal(original, MTextEscaper.Unescape(MTextEscaper.Escape(original)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Escape_HandlesNullAndEmpty(string? input)
    {
        Assert.Equal("", MTextEscaper.Escape(input));
        Assert.Equal("", MTextEscaper.Unescape(input));
    }

    [Fact]
    public void Unescape_LeavesUnknownCodesIntact()
    {
        // \L (start underline) is a real MText code this helper does not interpret; it must
        // pass through rather than being mangled.
        Assert.Equal("\\Lunder", MTextEscaper.Unescape("\\Lunder"));
    }
}
