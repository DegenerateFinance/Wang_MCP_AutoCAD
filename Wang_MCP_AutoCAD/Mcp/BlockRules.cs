namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The block decisions that do not need AutoCAD: which block table records a listing shows,
/// which ones may be redefined, and what happens to the source entities of a new block.
/// </summary>
public static class BlockRules
{
    /// <summary>Leave the source entities where they are.</summary>
    public const string SourceKeep = "keep";

    /// <summary>Erase the source entities.</summary>
    public const string SourceErase = "erase";

    /// <summary>
    /// Erase the source entities and insert a reference to the new block in their place —
    /// what AutoCAD's own BLOCK command does by default, so the drawing looks unchanged.
    /// </summary>
    public const string SourceReplace = "replace";

    public static IReadOnlyList<string> SourceActions { get; } = new[] { SourceReplace, SourceKeep, SourceErase };

    public const string DefaultSourceAction = SourceReplace;

    /// <summary>
    /// Whether acad_list_blocks shows a block table record.
    ///
    /// Layouts (*Model_Space, *Paper_Space) are never blocks a caller can insert. Anonymous
    /// records (*U dynamic variants, *D dimension blocks, *X hatch blocks) are internal
    /// plumbing and drown out the real blocks, so they need an opt-in. Xrefs are attached
    /// drawings rather than local definitions, so they need an opt-in too — and an unloaded
    /// xref has no content to report even then.
    /// </summary>
    public static bool ShouldList(
        bool isLayout,
        bool isAnonymous,
        bool isXref,
        bool isUnloaded,
        bool includeAnonymous,
        bool includeXrefs)
    {
        if (isLayout)
        {
            return false;
        }

        if (isAnonymous && !includeAnonymous)
        {
            return false;
        }

        if (isXref && (!includeXrefs || isUnloaded))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Refuses a redefinition the managed API cannot do correctly. Each refusal names the
    /// way forward, because "no" alone leaves a caller with nothing to try next.
    ///
    /// Dynamic blocks are the important one: their parameter/action graph is not exposed to
    /// .NET (verified by absence in acdbmgd.dll — no BlockParameter or BlockAction types), so
    /// swapping a dynamic block's entities would orphan its parameters and grips. Converting
    /// a reference to a static block first is the actionable alternative.
    /// </summary>
    public static bool TryCheckRedefinable(
        string name,
        bool isLayout,
        bool isXref,
        bool isAnonymous,
        bool isDynamic,
        out string error)
    {
        error = "";

        if (isLayout)
        {
            error = $"\"{name}\" is a layout, not a block, and cannot be redefined.";
            return false;
        }

        if (isXref)
        {
            error = $"\"{name}\" is an external reference. Edit the referenced drawing itself; "
                  + "an xref cannot be redefined from the host drawing.";
            return false;
        }

        if (isAnonymous)
        {
            error = $"\"{name}\" is an anonymous block (a dynamic block variant, dimension or "
                  + "hatch internal) and cannot be redefined by name.";
            return false;
        }

        if (isDynamic)
        {
            error = $"\"{name}\" is a dynamic block. Its parameters and actions are not "
                  + "accessible from .NET, so redefining it would break them. Convert a "
                  + "reference to a static block with acad_convert_block_to_static, then "
                  + "redefine that block instead.";
            return false;
        }

        return true;
    }
}
