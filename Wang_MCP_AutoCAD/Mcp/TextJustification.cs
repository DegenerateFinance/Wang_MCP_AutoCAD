namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>
/// The justification vocabulary the text tools expose, and its mapping to the integer values
/// of Autodesk's AttachmentPoint enum.
///
/// This is deliberately Autodesk-free and traffics in plain ints so the mapping is unit
/// testable — the test project never gets the AutoCAD DLLs. The Acad side casts the int to
/// AttachmentPoint. Verified against acdbmgd.dll metadata (assembly 25.1.0.0): AttachmentPoint
/// is the type of BOTH DBText.Justify and MText.Attachment, which is why one table serves
/// both tools.
///
/// Four families of the 24 AttachmentPoint values are deliberately NOT exposed:
///   - Align and Fit (BaseAlign, BaseFit, and their Bottom/Middle/Top variants) need a SECOND
///     point and either ignore the height or distort the width factor. Offering them with a
///     single position argument and an mm_height that silently stops applying would be a
///     worse experience than not offering them at all.
///   - The Mid family (BaseMid, MiddleMid, ...) is a near-duplicate of Center that differs
///     only in how the horizontal extent is measured; two names a model must choose between
///     for near-identical results hurts tool-selection accuracy more than it adds capability.
/// What remains is the 3x3 grid every drafter actually uses, plus the three baseline forms
/// that are AutoCAD's own defaults.
/// </summary>
public static class TextJustification
{
    /// <summary>
    /// The wire names, in the order they appear in the tool schema. Lowercase snake_case to
    /// match every other enum in this surface rather than AutoCAD's PascalCase.
    /// </summary>
    public static IReadOnlyList<string> Allowed { get; } = new[]
    {
        "left",
        "center",
        "right",
        "top_left",
        "top_center",
        "top_right",
        "middle_left",
        "middle_center",
        "middle_right",
        "bottom_left",
        "bottom_center",
        "bottom_right",
    };

    /// <summary>
    /// AutoCAD's own default for new text: the insertion point is the left end of the
    /// baseline. Named rather than inlined because both text tools default to it.
    /// </summary>
    public const string DefaultJustification = "left";

    /// <summary>
    /// MText's default attachment. Differs from <see cref="DefaultJustification"/> on purpose:
    /// AutoCAD's MTEXT command attaches at the top left, and a paragraph that grows downward
    /// from where the caller put it is what a caller expects. A DBText baseline-left default
    /// and an MText top-left default both match the commands they stand in for.
    /// </summary>
    public const string DefaultAttachment = "top_left";

    /// <summary>
    /// Maps a wire name to the integer value of the matching AttachmentPoint member. Matching
    /// is case-insensitive.
    /// </summary>
    public static bool TryGetAttachmentPoint(string? name, out int attachmentPoint, out string error)
    {
        attachmentPoint = 0;
        error = "";

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "A justification name is required. It must be one of: "
                  + string.Join(", ", Allowed) + ".";
            return false;
        }

        switch (name.Trim().ToLowerInvariant())
        {
            // The baseline family: AutoCAD's BaseLeft is the value a plain DBText carries.
            case "left": attachmentPoint = 10; return true;   // BaseLeft
            case "center": attachmentPoint = 11; return true; // BaseCenter
            case "right": attachmentPoint = 12; return true;  // BaseRight

            case "top_left": attachmentPoint = 1; return true;
            case "top_center": attachmentPoint = 2; return true;
            case "top_right": attachmentPoint = 3; return true;

            case "middle_left": attachmentPoint = 4; return true;
            case "middle_center": attachmentPoint = 5; return true;
            case "middle_right": attachmentPoint = 6; return true;

            case "bottom_left": attachmentPoint = 7; return true;
            case "bottom_center": attachmentPoint = 8; return true;
            case "bottom_right": attachmentPoint = 9; return true;

            default:
                error = $"Unknown justification \"{name}\". It must be one of: "
                      + string.Join(", ", Allowed) + ".";
                return false;
        }
    }

    /// <summary>
    /// The reverse mapping, for reporting back what was actually applied. An AttachmentPoint
    /// this surface does not expose — a value read off pre-existing text, such as one of the
    /// Fit or Align forms — is rendered as its AutoCAD name lowercased rather than refused,
    /// because a read must never fail on geometry someone else drew.
    /// </summary>
    public static string DescribeAttachmentPoint(int attachmentPoint)
    {
        return attachmentPoint switch
        {
            1 => "top_left",
            2 => "top_center",
            3 => "top_right",
            4 => "middle_left",
            5 => "middle_center",
            6 => "middle_right",
            7 => "bottom_left",
            8 => "bottom_center",
            9 => "bottom_right",
            10 => "left",
            11 => "center",
            12 => "right",
            13 => "base_align",
            14 => "bottom_align",
            15 => "middle_align",
            16 => "top_align",
            17 => "base_fit",
            18 => "bottom_fit",
            19 => "middle_fit",
            20 => "top_fit",
            21 => "base_mid",
            22 => "bottom_mid",
            23 => "middle_mid",
            24 => "top_mid",
            _ => $"unknown_{attachmentPoint}",
        };
    }

    /// <summary>
    /// True when the justification leaves DBText.Position authoritative. Only the plain
    /// baseline-left case does.
    ///
    /// This is THE trap in AutoCAD's text API: for every other justification AutoCAD reads
    /// AlignmentPoint and ignores Position, so text written with only Position set lands at
    /// the origin or at the previous alignment point. Callers of this must set BOTH points and
    /// then call AdjustAlignment(db). Exposed as a named predicate rather than an inline
    /// comparison so the rule is stated once and can be asserted in a test.
    /// </summary>
    public static bool UsesPositionOnly(int attachmentPoint)
    {
        return attachmentPoint == 10;
    }
}
