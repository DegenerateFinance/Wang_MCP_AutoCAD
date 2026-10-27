using System.Diagnostics;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Wang_MCP_AutoCAD.Acad.Support;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad;

/// <summary>
/// Creating and editing text: single-line DBText, multi-line MText, and retargeting the
/// content of either one (or of a block attribute) by handle.
///
/// Two AutoCAD behaviours drive the shape of everything here and are worth stating once:
///   - A DBText whose justification is anything other than baseline-left takes its location
///     from AlignmentPoint, NOT Position. See <see cref="ApplyJustification"/>.
///   - MText.Contents is a formatting language, not plain text, so a caller's string is
///     escaped by default. See <see cref="MTextEscaper"/>.
/// </summary>
internal static class TextTools
{
    /// <summary>
    /// Matches the cap on acad_set_entity_properties: the same "act on what you queried"
    /// contract, so the same bound.
    /// </summary>
    private const int MaxHandles = 1000;

    /// <summary>
    /// A generous but finite cap on a single text string. Without one, a runaway caller can
    /// push megabytes into a drawing one tool call at a time.
    /// </summary>
    private const int MaxTextLength = 32000;

    public static void RegisterAll(ToolRegistry registry)
    {
        registry.Add(CreateAddTextTool());
        registry.Add(CreateAddMTextTool());
        registry.Add(CreateSetTextContentTool());
    }

    // ---- acad_add_text ----------------------------------------------------------------------

    private static McpTool CreateAddTextTool()
    {
        return new McpTool
        {
            Name = "acad_add_text",
            Description =
                "Adds a single-line TEXT entity. Use this for labels, tags and callouts; for a "
                + "wrapping paragraph use acad_add_mtext instead. The position means different "
                + "things depending on justification: for \"left\" it is the left end of the "
                + "baseline, otherwise it is the alignment point named by the justification (so "
                + "\"middle_center\" centres the text on the point). Heights are millimetres and "
                + "rotation is degrees counter-clockwise. Returns the new entity's handle.",
            InputSchema = ToolSchema.Object()
                .String("text", "The text to display. A single line; newlines are not supported — use acad_add_mtext.", required: true)
                .PointMm("mm_position", "The insertion or alignment point.", required: true)
                .PositiveNumber("mm_height", "Cap height of the text, in millimetres. Ignored if the text style has a fixed height, which is reported back as height_overridden_by_style.", required: true)
                .Number("deg_rotation", "Rotation in degrees, counter-clockwise from the positive X axis. Defaults to 0.")
                .StringEnum(
                    "justify",
                    "Which point of the text sits at mm_position.",
                    TextJustification.Allowed,
                    defaultValue: TextJustification.DefaultJustification)
                .String("text_style", "Text style name. Must already exist; defaults to the drawing's current style.")
                .PositiveNumber("width_factor", "Horizontal stretch factor. Dimensionless; 1 is normal, 0.8 is condensed.")
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Text,
            Handler = AddText,
        };
    }

    private static ToolResult AddText(ToolArgs args)
    {
        if (!TryReadTextString(args, "text", out string text, out string error))
        {
            return ToolResult.Fail(error);
        }

        // A newline in a DBText is not a line break, it is a glyph AutoCAD cannot render.
        // Rejecting beats silently drawing a box character and pointing at the right tool.
        if (text.Contains('\n') || text.Contains('\r'))
        {
            return ToolResult.Fail(
                "Argument \"text\" contains a line break, but a TEXT entity is single-line. "
                + "Use acad_add_mtext for multi-line text.");
        }

        if (!PointArgs.TryReadPointMm(args, "mm_position", required: true, out Mm_Point mm_Position, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetPositiveDouble("mm_height", out double mm_Height, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetDoubleOrDefault("deg_rotation", 0, out double deg_Rotation, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetEnum("justify", TextJustification.Allowed, TextJustification.DefaultJustification, out string justify, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!TextJustification.TryGetAttachmentPoint(justify, out int attachmentPoint, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("text_style", null, out string? textStyle, out error))
        {
            return ToolResult.Fail(error);
        }

        bool hasWidthFactor = args.HasArgument("width_factor");
        double widthFactor = 1;
        if (hasWidthFactor && !args.TryGetPositiveDouble("width_factor", out widthFactor, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;
        string resolvedStyle;
        bool heightOverridden;
        double mm_ActualHeight;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryResolveTextStyle(tr, acad.Db, textStyle, out ObjectId styleId, out double du_StyleHeight, out resolvedStyle, out error))
                {
                    return ToolResult.Fail(error);
                }

                DBText dbText = new();
                dbText.SetDatabaseDefaults();

                dbText.TextStyleId = styleId;
                dbText.TextString = text;
                dbText.Height = acad.ToDu(mm_Height);
                dbText.Rotation = Units.DegToRad(deg_Rotation);

                if (hasWidthFactor)
                {
                    // WidthFactor is dimensionless and must NOT be run through ToDu.
                    dbText.WidthFactor = widthFactor;
                }

                ApplyJustification(acad, dbText, attachmentPoint, mm_Position);

                dbText.Layer = resolvedLayer;

                target.AppendEntity(dbText);
                tr.AddNewlyCreatedDBObject(dbText, true);

                // Only once the text is database-resident; see ApplyJustification.
                AdjustIfJustified(dbText, acad.Db);

                // A style with a non-zero TextSize forces its own height and silently discards
                // the one just set; report that rather than letting the caller believe mm_height
                // took effect.
                heightOverridden = du_StyleHeight > 0;
                mm_ActualHeight = acad.ToMm(dbText.Height);

                handle = dbText.Handle.ToString();

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/add_text outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the text: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/add_text outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the text; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/add_text handle={handle} layer={resolvedLayer} justify={justify} "
            + $"chars={text.Length} height_overridden={heightOverridden} entities=1 "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["text"] = text;
        payload["mm_position"] = ToMmArray(mm_Position);
        payload["mm_height"] = mm_ActualHeight;
        payload["deg_rotation"] = Units.NormalizeDeg(deg_Rotation);
        payload["justify"] = justify;
        payload["text_style"] = resolvedStyle;
        payload["height_overridden_by_style"] = heightOverridden;

        return ToolResult.Ok(payload);
    }

    /// <summary>
    /// Positions a DBText, working around the single biggest trap in AutoCAD's text API.
    ///
    /// For baseline-left text, Position is authoritative and AlignmentPoint is ignored. For
    /// EVERY other justification it is the other way round: AutoCAD reads AlignmentPoint and
    /// ignores Position, so setting only Position leaves the text at the origin. Setting both
    /// and then calling AdjustAlignment(db) is the documented way to be correct in both cases
    /// — AdjustAlignment recomputes whichever of the two is the derived one.
    ///
    /// Justify must be assigned BEFORE the points: it is what decides which of them AutoCAD
    /// will treat as authoritative.
    ///
    /// This sets Justify and the points ONLY. The caller must call
    /// <c>AdjustAlignment(db)</c> itself AFTER the entity is appended and
    /// AddNewlyCreatedDBObject'd. Verified live (Phase 4): AdjustAlignment on an
    /// AttributeDefinition not yet in the database throws eInvalidContext, even with the
    /// alternate-database argument. Calling it after the append is AutoCAD's own documented
    /// order, so DBText follows it too rather than relying on the pre-append call happening
    /// to work there.
    ///
    /// Internal rather than private because BlockSupport positions AttributeDefinitions —
    /// which derive from DBText and fall into exactly the same trap — through it too.
    /// </summary>
    internal static void ApplyJustification(
        AcadContext acad,
        DBText dbText,
        int attachmentPoint,
        Mm_Point mm_Position)
    {
        dbText.Justify = (AttachmentPoint)attachmentPoint;

        Autodesk.AutoCAD.Geometry.Point3d du_Point = acad.ToPoint3d(mm_Position);

        dbText.Position = du_Point;

        if (!TextJustification.UsesPositionOnly(attachmentPoint))
        {
            dbText.AlignmentPoint = du_Point;
        }
    }

    /// <summary>
    /// Calls AdjustAlignment only when it does something: for baseline-left text Position is
    /// authoritative and there is nothing to recompute. Skipping it there keeps the common
    /// case clear of AdjustAlignment's context sensitivity (eInvalidContext, seen live in
    /// Phase 4), which the gateway's working-database switch is meant to cure for the rest.
    /// Must be called on a database-resident entity.
    /// </summary>
    internal static void AdjustIfJustified(DBText dbText, Database db)
    {
        if (TextJustification.UsesPositionOnly((int)dbText.Justify))
        {
            return;
        }

        dbText.AdjustAlignment(db);
    }

    // ---- acad_add_mtext ---------------------------------------------------------------------

    private static McpTool CreateAddMTextTool()
    {
        return new McpTool
        {
            Name = "acad_add_mtext",
            Description =
                "Adds a multi-line MTEXT paragraph. Newlines in the text become line breaks. "
                + "Pass mm_width to wrap at a column width, or 0 to let each line run as long "
                + "as it needs. The text is escaped so it displays literally; set "
                + "escape_formatting to false only if you are deliberately passing MText "
                + "formatting codes such as \\P or \\pxi. attachment names which corner or "
                + "edge of the paragraph sits at mm_location. Returns the handle and the "
                + "measured size of the result.",
            InputSchema = ToolSchema.Object()
                .String("text", "The text to display. Newlines become line breaks.", required: true)
                .PointMm("mm_location", "The attachment point of the paragraph.", required: true)
                .PositiveNumber("mm_height", "Cap height of the text, in millimetres.", required: true)
                .Number("mm_width", "Column width to wrap at, in millimetres. 0 (the default) means no wrapping.")
                .Number("deg_rotation", "Rotation in degrees, counter-clockwise from the positive X axis. Defaults to 0.")
                .StringEnum(
                    "attachment",
                    "Which point of the paragraph sits at mm_location.",
                    TextJustification.Allowed,
                    defaultValue: TextJustification.DefaultAttachment)
                .Boolean(
                    "escape_formatting",
                    "Escape the text so backslashes and braces display literally instead of being read as MText formatting codes.",
                    true)
                .String("text_style", "Text style name. Must already exist; defaults to the drawing's current style.")
                .String("layer", "Layer to draw on. Must already exist; defaults to the current layer.")
                .StringEnum("space", "Which space to draw into.", EntityResolver.AllowedSpaces, defaultValue: EntityResolver.DefaultSpace)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Text,
            Handler = AddMText,
        };
    }

    private static ToolResult AddMText(ToolArgs args)
    {
        if (!TryReadTextString(args, "text", out string text, out string error))
        {
            return ToolResult.Fail(error);
        }

        if (!PointArgs.TryReadPointMm(args, "mm_location", required: true, out Mm_Point mm_Location, out _, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetPositiveDouble("mm_height", out double mm_Height, out error))
        {
            return ToolResult.Fail(error);
        }

        // Width is NOT TryGetPositiveDouble: 0 is meaningful and means "do not wrap".
        if (!args.TryGetDoubleOrDefault("mm_width", 0, out double mm_Width, out error))
        {
            return ToolResult.Fail(error);
        }

        if (mm_Width < 0)
        {
            return ToolResult.Fail(
                $"Argument \"mm_width\" must be 0 or greater, but was {mm_Width}. Use 0 for no wrapping.");
        }

        if (!args.TryGetDoubleOrDefault("deg_rotation", 0, out double deg_Rotation, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetEnum("attachment", TextJustification.Allowed, TextJustification.DefaultAttachment, out string attachment, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!TextJustification.TryGetAttachmentPoint(attachment, out int attachmentPoint, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetBoolOrDefault("escape_formatting", true, out bool escapeFormatting, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!args.TryGetStringOrDefault("text_style", null, out string? textStyle, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!TryReadPlacement(args, out string? layer, out string space, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        string contents = escapeFormatting ? MTextEscaper.Escape(text) : text;

        Stopwatch sw = Stopwatch.StartNew();
        string handle;
        string resolvedLayer;
        string resolvedStyle;
        double mm_ActualWidth;
        double mm_ActualHeight;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveLayer(tr, acad.Db, layer, out resolvedLayer, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!EntityResolver.TryResolveSpace(tr, acad.Db, space, OpenMode.ForWrite, out BlockTableRecord target, out error))
                {
                    return ToolResult.Fail(error);
                }

                if (!TryResolveTextStyle(tr, acad.Db, textStyle, out ObjectId styleId, out double _, out resolvedStyle, out error))
                {
                    return ToolResult.Fail(error);
                }

                MText mText = new();
                mText.SetDatabaseDefaults();

                mText.TextStyleId = styleId;
                mText.TextHeight = acad.ToDu(mm_Height);

                // Width before Contents so the first layout already wraps; Width = 0 is
                // AutoCAD's own "no wrapping" sentinel and needs no special-casing.
                mText.Width = acad.ToDu(mm_Width);
                mText.Contents = contents;

                mText.Attachment = (AttachmentPoint)attachmentPoint;
                mText.Location = acad.ToPoint3d(mm_Location);
                mText.Rotation = Units.DegToRad(deg_Rotation);

                mText.Layer = resolvedLayer;

                target.AppendEntity(mText);
                tr.AddNewlyCreatedDBObject(mText, true);

                // ActualWidth/ActualHeight are the measured extent of the laid-out paragraph
                // and are only populated once the entity is in the database — read them here,
                // after the append and before the commit, or they come back as 0.
                mm_ActualWidth = acad.ToMm(mText.ActualWidth);
                mm_ActualHeight = acad.ToMm(mText.ActualHeight);

                handle = mText.Handle.ToString();

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/add_mtext outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail($"AutoCAD rejected the mtext: {acEx.ErrorStatus}.");
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/add_mtext outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not create the mtext; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/add_mtext handle={handle} layer={resolvedLayer} attachment={attachment} "
            + $"chars={text.Length} escaped={escapeFormatting} mm_width={mm_Width} entities=1 "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["handle"] = handle;
        payload["layer"] = resolvedLayer;
        payload["text"] = text;
        payload["contents"] = contents;
        payload["escaped"] = escapeFormatting;
        payload["mm_location"] = ToMmArray(mm_Location);
        payload["mm_height"] = mm_Height;
        payload["mm_width"] = mm_Width;
        payload["wrapped"] = mm_Width > 0;
        payload["mm_actual_width"] = mm_ActualWidth;
        payload["mm_actual_height"] = mm_ActualHeight;
        payload["deg_rotation"] = Units.NormalizeDeg(deg_Rotation);
        payload["attachment"] = attachment;
        payload["text_style"] = resolvedStyle;

        return ToolResult.Ok(payload);
    }

    // ---- acad_set_text_content --------------------------------------------------------------

    private static McpTool CreateSetTextContentTool()
    {
        return new McpTool
        {
            Name = "acad_set_text_content",
            Description =
                "Replaces the string shown by existing text, by handle. Works on single-line "
                + "TEXT, multi-line MTEXT and block attributes (ATTRIB). Every handle must name "
                + "one of those; if any handle cannot be resolved or is not text, nothing is "
                + "changed. Use acad_set_entity_properties to change a text entity's layer or "
                + "colour, and acad_get_entity_properties to read its current content.",
            InputSchema = ToolSchema.Object()
                .StringArray(
                    "handles",
                    "Handles of the text entities to change. All are set to the same text.",
                    minItems: 1,
                    maxItems: MaxHandles,
                    required: true)
                .String("text", "The replacement text.", required: true)
                .Boolean(
                    "escape_formatting",
                    "For MTEXT and multiline attributes, escape the text so it displays literally instead of being read as MText formatting codes. Has no effect on single-line TEXT.",
                    true)
                .Build(),
            RequiresDocument = true,
            Group = ToolGroups.Text,
            Handler = SetTextContent,
        };
    }

    private static ToolResult SetTextContent(ToolArgs args)
    {
        if (!args.TryGetStringArray("handles", 1, MaxHandles, out IReadOnlyList<string> handles, out string error))
        {
            return ToolResult.Fail(error);
        }

        // Deliberately not TryReadTextString's required-non-empty form for the value itself:
        // clearing an attribute to "" is a legitimate edit, unlike creating empty text.
        if (!args.TryGetStringOrDefault("text", null, out string? rawText, out error))
        {
            return ToolResult.Fail(error);
        }

        if (rawText is null)
        {
            return ToolResult.Fail("Required argument \"text\" is missing.");
        }

        if (rawText.Length > MaxTextLength)
        {
            return ToolResult.Fail(
                $"Argument \"text\" is {rawText.Length} characters, which exceeds the {MaxTextLength} "
                + "character limit.");
        }

        string text = rawText;

        if (!args.TryGetBoolOrDefault("escape_formatting", true, out bool escapeFormatting, out error))
        {
            return ToolResult.Fail(error);
        }

        if (!AcadContext.TryOpen(out AcadContext acad, out ToolResult? failure))
        {
            return failure!;
        }

        Stopwatch sw = Stopwatch.StartNew();
        JsonArray changed = new();
        int singleLine = 0;
        int multiLine = 0;
        int attributes = 0;

        try
        {
            using (Transaction tr = acad.Db.TransactionManager.StartTransaction())
            {
                if (!EntityResolver.TryResolveHandles(
                        tr,
                        acad.Db,
                        handles,
                        OpenMode.ForWrite,
                        requireAll: true,
                        out List<Entity> entities,
                        out JsonArray _,
                        out error))
                {
                    return ToolResult.Fail(error);
                }

                // Validate the whole set BEFORE mutating any of it: this tool is
                // all-or-nothing like the other mutators, and a non-text handle found halfway
                // through must not leave the first half already rewritten.
                if (!TryRejectNonText(entities, out error))
                {
                    return ToolResult.Fail(error);
                }

                foreach (Entity entity in entities)
                {
                    switch (entity)
                    {
                        // AttributeReference must precede DBText: it derives from DBText, so a
                        // DBText arm first would swallow it and skip UpdateMTextAttribute.
                        case AttributeReference attribute:
                            ApplyToAttribute(attribute, text, escapeFormatting);
                            attributes++;
                            break;

                        case MText mText:
                            mText.Contents = escapeFormatting ? MTextEscaper.Escape(text) : text;
                            multiLine++;
                            break;

                        case DBText dbText:
                            // A DBText cannot hold a line break; fail rather than write a
                            // string AutoCAD will render as a box glyph.
                            if (text.Contains('\n') || text.Contains('\r'))
                            {
                                error = $"Handle {dbText.Handle} is a single-line TEXT entity, but "
                                      + "the replacement text contains a line break. Remove the "
                                      + "line break, or replace the entity with acad_add_mtext.";
                                return ToolResult.Fail(error);
                            }

                            dbText.TextString = text;
                            AdjustIfJustified(dbText, acad.Db);
                            singleLine++;
                            break;
                    }

                    changed.Add(entity.Handle.ToString());
                }

                tr.Commit();
            }
        }
        catch (Autodesk.AutoCAD.Runtime.Exception acEx)
        {
            sw.Stop();
            Log.Error($"op=tool/set_text_content outcome=acad_error error_status={acEx.ErrorStatus} elapsed_ms={sw.ElapsedMilliseconds}", acEx);
            return ToolResult.Fail(DescribeAcadFailure(acEx));
        }
        catch (System.Exception ex)
        {
            sw.Stop();
            Log.Error($"op=tool/set_text_content outcome=failed elapsed_ms={sw.ElapsedMilliseconds}", ex);
            return ToolResult.Fail("Could not change the text; see the MCP log.");
        }

        sw.Stop();
        Log.Info(
            $"op=tool/set_text_content changed={changed.Count} text_entities={singleLine} "
            + $"mtext_entities={multiLine} attributes={attributes} chars={text.Length} "
            + $"elapsed_ms={sw.ElapsedMilliseconds}");

        JsonObject payload = acad.NewPayload();
        payload["changed"] = changed.Count;
        payload["handles"] = changed;
        payload["text"] = text;
        payload["text_entities"] = singleLine;
        payload["mtext_entities"] = multiLine;
        payload["attributes"] = attributes;

        return ToolResult.Ok(payload);
    }

    /// <summary>
    /// Writes a value to a block attribute. A multiline attribute keeps its content in a
    /// nested MText, and changing TextString alone leaves that nested object stale — the
    /// drawing then still displays the OLD value. UpdateMTextAttribute() is what pushes the
    /// new string through, and is a no-op worth calling only for the multiline case.
    /// </summary>
    private static void ApplyToAttribute(
        AttributeReference attribute,
        string text,
        bool escapeFormatting)
    {
        if (attribute.IsMTextAttribute)
        {
            attribute.TextString = escapeFormatting ? MTextEscaper.Escape(text) : text;
            attribute.UpdateMTextAttribute();
            return;
        }

        attribute.TextString = text;
    }

    /// <summary>
    /// Rejects the whole call if any resolved entity is not something this tool can write to.
    /// Names every offender and its type, so a caller correcting itself does not have to
    /// re-run the call once per bad handle.
    /// </summary>
    private static bool TryRejectNonText(IReadOnlyList<Entity> entities, out string error)
    {
        error = "";
        List<string> offenders = new();

        foreach (Entity entity in entities)
        {
            // AttributeDefinition derives from DBText but lives in a block definition, where
            // rewriting it would not change any existing reference — excluded on purpose so
            // the effect of this tool is never invisible.
            if (entity is AttributeDefinition or not (DBText or MText))
            {
                offenders.Add($"{entity.Handle} ({entity.GetRXClass().DxfName})");
            }
        }

        if (offenders.Count == 0)
        {
            return true;
        }

        error = $"{offenders.Count} of {entities.Count} handles do not name text that can be "
              + $"changed, so nothing was changed: {string.Join(", ", offenders)}. This tool "
              + "works on TEXT, MTEXT and ATTRIB entities.";
        return false;
    }

    private static string DescribeAcadFailure(Autodesk.AutoCAD.Runtime.Exception acEx)
    {
        if (acEx.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
        {
            return "At least one entity is on a locked layer and cannot be changed. Unlock the "
                 + "layer with acad_set_layer_state first.";
        }

        return $"AutoCAD rejected the change: {acEx.ErrorStatus}.";
    }

    // ---- shared -----------------------------------------------------------------------------

    /// <summary>
    /// Reads a required, non-empty text argument and bounds its length. Whitespace-only is
    /// rejected by TryGetString: text made of spaces is invisible in the drawing and is never
    /// what a caller meant to create.
    /// </summary>
    private static bool TryReadTextString(
        ToolArgs args,
        string name,
        out string text,
        out string error)
    {
        if (!args.TryGetString(name, out text, out error))
        {
            return false;
        }

        if (text.Length > MaxTextLength)
        {
            error = $"Argument \"{name}\" is {text.Length} characters, which exceeds the "
                  + $"{MaxTextLength} character limit.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Resolves an optional text style name. A null name means the drawing's current style.
    /// Also hands back the style's fixed height, where a non-zero value means the style
    /// overrides whatever height the caller asked for.
    /// </summary>
    private static bool TryResolveTextStyle(
        Transaction tr,
        Database db,
        string? styleName,
        out ObjectId styleId,
        out double du_StyleHeight,
        out string resolvedStyle,
        out string error)
    {
        styleId = ObjectId.Null;
        du_StyleHeight = 0;
        resolvedStyle = "";
        error = "";

        TextStyleTable table = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);

        if (styleName is null)
        {
            styleId = db.Textstyle;
        }
        else
        {
            if (!table.Has(styleName))
            {
                error = $"Text style \"{styleName}\" does not exist in this drawing. Create it in "
                      + "AutoCAD with the STYLE command, or omit text_style to use the current style.";
                return false;
            }

            styleId = table[styleName];
        }

        TextStyleTableRecord record = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
        du_StyleHeight = record.TextSize;
        resolvedStyle = record.Name;
        return true;
    }

    /// <summary>Reads the layer and space arguments every creation tool shares.</summary>
    private static bool TryReadPlacement(
        ToolArgs args,
        out string? layer,
        out string space,
        out string error)
    {
        space = EntityResolver.DefaultSpace;

        if (!args.TryGetStringOrDefault("layer", null, out layer, out error))
        {
            return false;
        }

        return args.TryGetEnum(
            "space",
            EntityResolver.AllowedSpaces,
            EntityResolver.DefaultSpace,
            out space,
            out error);
    }

    private static JsonArray ToMmArray(Mm_Point point)
    {
        return new JsonArray { point.Mm_X, point.Mm_Y, point.Mm_Z };
    }
}
