namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// Symbol-name rules for layers and blocks.
///
/// AutoCAD's own SymbolUtilityServices.ValidateSymbolName throws rather than returning a
/// bool, and lives on the Autodesk side where no test can reach it. Checking the same rules
/// here first means a bad name produces a message naming the offending character instead of
/// an eInvalidSymbolTableName escaping a transaction — and the rules stay unit-testable.
/// </summary>
public static class SymbolNaming
{
    /// <summary>
    /// The characters AutoCAD forbids in a symbol name. The vertical bar is excluded
    /// separately: it is the xref dependency separator, so a name containing one is not
    /// merely invalid but means something else entirely.
    /// </summary>
    private static readonly char[] Forbidden =
    {
        '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', ',', '=', '`',
    };

    /// <summary>AutoCAD's limit for a symbol table record name.</summary>
    public const int MaxLength = 255;

    public static bool TryValidate(string? name, string kind, out string error)
    {
        error = "";

        if (string.IsNullOrWhiteSpace(name))
        {
            error = $"The {kind} name must not be blank.";
            return false;
        }

        if (name.Length > MaxLength)
        {
            error = $"The {kind} name is {name.Length} characters; the maximum is {MaxLength}.";
            return false;
        }

        int index = name.IndexOfAny(Forbidden);
        if (index >= 0)
        {
            error = $"The {kind} name \"{name}\" contains the character '{name[index]}', which "
                  + "AutoCAD does not allow. Names cannot contain < > / \\ \" : ; ? * | , = ` "
                  + "and cannot be blank.";
            return false;
        }

        return true;
    }
}

/// <summary>Layer-specific naming rules, including the reserved names.</summary>
public static class LayerNaming
{
    public const string Zero = "0";
    public const string Defpoints = "Defpoints";

    public static bool TryValidate(string? name, out string error)
    {
        return SymbolNaming.TryValidate(name, "layer", out error);
    }

    /// <summary>
    /// True for the layers AutoCAD reserves. Layer 0 cannot be renamed or deleted, and
    /// Defpoints is special-cased by AutoCAD's plotting.
    /// </summary>
    public static bool IsReserved(string? name)
    {
        return string.Equals(name, Zero, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, Defpoints, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Block-specific naming rules.</summary>
public static class BlockNaming
{
    public static bool TryValidate(string? name, out string error)
    {
        error = "";

        // The asterisk check comes BEFORE the general one. '*' is also in the forbidden
        // character list, so checking generically first would reject "*U3" with a message
        // about illegal characters — true, but it hides the actual reason: that name is a
        // reserved anonymous block, not a typo the caller should fix by dropping a character.
        if (name is not null && name.StartsWith('*'))
        {
            error = $"Block name \"{name}\" starts with '*', which AutoCAD reserves for "
                  + "anonymous blocks such as dynamic block variants and layout records. "
                  + "Choose a name that does not begin with an asterisk.";
            return false;
        }

        return SymbolNaming.TryValidate(name, "block", out error);
    }

    /// <summary>True for the anonymous and layout records that are not insertable by name.</summary>
    public static bool IsAnonymousOrLayout(string? name)
    {
        return name is not null && name.StartsWith('*');
    }
}
