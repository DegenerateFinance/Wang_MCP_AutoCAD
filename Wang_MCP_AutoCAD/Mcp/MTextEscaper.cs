using System.Text;

namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// MText.Contents is not plain text: it is a formatting language where backslash introduces
/// a code, braces group, and \P is a paragraph break. A caller passing a Windows path or a
/// LaTeX-ish string into it gets silent corruption rather than an error.
///
/// Escape() makes a plain string display literally. Tools expose escape_formatting (default
/// true) so a caller that genuinely wants formatting codes can opt out.
/// </summary>
public static class MTextEscaper
{
    /// <summary>
    /// Escapes the three characters MText treats as syntax and converts newlines to \P.
    ///
    /// Order matters: backslashes must be doubled first, or the backslash introduced by the
    /// brace escaping would itself be doubled on a second pass.
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        StringBuilder builder = new(text.Length + 16);

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            switch (c)
            {
                case '\\':
                    builder.Append("\\\\");
                    break;

                case '{':
                    builder.Append("\\{");
                    break;

                case '}':
                    builder.Append("\\}");
                    break;

                case '\r':
                    // Swallow CR; a CRLF pair becomes a single \P via the LF below.
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        break;
                    }

                    builder.Append("\\P");
                    break;

                case '\n':
                    builder.Append("\\P");
                    break;

                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// Renders MText contents back to approximate plain text: undoes the escapes Escape()
    /// applies. This is a convenience for reporting, not a full MText parser — it does not
    /// interpret font, height or colour codes.
    /// </summary>
    public static string Unescape(string? contents)
    {
        if (string.IsNullOrEmpty(contents))
        {
            return "";
        }

        StringBuilder builder = new(contents.Length);

        for (int i = 0; i < contents.Length; i++)
        {
            char c = contents[i];

            if (c != '\\' || i + 1 >= contents.Length)
            {
                builder.Append(c);
                continue;
            }

            char next = contents[i + 1];

            switch (next)
            {
                case 'P':
                    builder.Append('\n');
                    i++;
                    break;

                case '\\':
                case '{':
                case '}':
                    builder.Append(next);
                    i++;
                    break;

                default:
                    builder.Append(c);
                    break;
            }
        }

        return builder.ToString();
    }
}
