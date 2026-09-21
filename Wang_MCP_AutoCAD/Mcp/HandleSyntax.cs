namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Syntax checking for AutoCAD entity handles, which are hexadecimal strings.
///
/// Validating the shape before touching the database is what turns an opaque eInvalidInput
/// (or a FormatException out of Convert.ToInt64) into a message that tells the model where
/// to get a real handle. Autodesk-free on purpose, so the rules are unit-testable.
/// </summary>
public static class HandleSyntax
{
    /// <summary>
    /// A handle is a 64-bit value rendered as hex, so it cannot exceed 16 digits. Anything
    /// longer is a malformed argument rather than a handle this drawing might not contain.
    /// </summary>
    private const int MaxHexDigits = 16;

    /// <summary>
    /// Trims, strips an optional "0x", and uppercases. Returns false — with a message naming
    /// the offending input — for anything that cannot be a handle at all.
    /// </summary>
    public static bool TryNormalize(string? raw, out string normalized, out string error)
    {
        normalized = "";
        error = "";

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "A handle was empty. Handles are hexadecimal strings such as \"1A7\"; "
                  + "use acad_list_entities to get the handle of an entity.";
            return false;
        }

        string candidate = raw.Trim();

        if (candidate.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate.Substring(2);
        }

        if (candidate.Length == 0)
        {
            error = Describe(raw);
            return false;
        }

        if (candidate.Length > MaxHexDigits)
        {
            error = $"Handle \"{raw}\" is too long to be valid — a handle is at most "
                  + $"{MaxHexDigits} hexadecimal digits.";
            return false;
        }

        foreach (char c in candidate)
        {
            if (!IsHexDigit(c))
            {
                error = Describe(raw);
                return false;
            }
        }

        string upper = candidate.ToUpperInvariant();

        // "0" is the null handle: syntactically hex, but it never names an entity, and
        // letting it through produces a confusing "no such object" far from the cause.
        if (upper.TrimStart('0').Length == 0)
        {
            error = $"Handle \"{raw}\" is the null handle and does not name an entity. "
                  + "Use acad_list_entities to get the handle of an entity.";
            return false;
        }

        normalized = upper;
        return true;
    }

    public static bool IsWellFormed(string? raw)
    {
        return TryNormalize(raw, out _, out _);
    }

    /// <summary>The standard message for a handle that is not hexadecimal.</summary>
    public static string Describe(string? raw)
    {
        return $"Handle \"{raw}\" is not valid — handles are hexadecimal strings such as \"1A7\". "
             + "Use acad_list_entities to get the handle of an entity.";
    }

    private static bool IsHexDigit(char c)
    {
        return (c >= '0' && c <= '9')
            || (c >= 'a' && c <= 'f')
            || (c >= 'A' && c <= 'F');
    }
}
