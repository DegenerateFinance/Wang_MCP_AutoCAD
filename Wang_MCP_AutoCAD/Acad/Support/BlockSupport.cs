using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad.Support;

/// <summary>The outcome of syncing one reference's attributes to its definition.</summary>
internal readonly record struct ReferenceSyncResult(
    int Preserved,
    int Reset,
    IReadOnlyList<string> AddedTags,
    IReadOnlyList<string> RemovedTags);

/// <summary>
/// Block plumbing shared by the block tools. Every decision that does not need AutoCAD —
/// which tag gets which value, what a sync keeps — lives in the Mcp planners
/// (AttributeAssignment, AttributeSyncPlan); this file only carries those plans out.
/// </summary>
internal static class BlockSupport
{
    /// <summary>Resolves a block definition by name, with a message that points at acad_list_blocks.</summary>
    public static bool TryResolveDefinition(
        Transaction tr,
        Database db,
        string name,
        OpenMode openMode,
        out BlockTableRecord definition,
        out string error)
    {
        definition = null!;
        error = "";

        BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        if (!blockTable.Has(name))
        {
            error = $"Block \"{name}\" does not exist in this drawing. Call acad_list_blocks for "
                  + "the available names.";
            return false;
        }

        definition = (BlockTableRecord)tr.GetObject(blockTable[name], openMode);
        return true;
    }

    /// <summary>
    /// The name a caller can insert by. A dynamic block whose properties have been changed
    /// points at an anonymous "*U" variant; DynamicBlockTableRecord still names the real one.
    /// </summary>
    public static string GetEffectiveName(Transaction tr, BlockReference reference)
    {
        ObjectId definitionId = reference.IsDynamicBlock
            ? reference.DynamicBlockTableRecord
            : reference.BlockTableRecord;

        BlockTableRecord definition = (BlockTableRecord)tr.GetObject(definitionId, OpenMode.ForRead);
        return definition.Name;
    }

    /// <summary>The attribute definitions of a block, in the definition's own order.</summary>
    public static List<AttributeDefinition> GetAttributeDefinitions(Transaction tr, BlockTableRecord definition)
    {
        List<AttributeDefinition> result = new();

        foreach (ObjectId id in definition)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is AttributeDefinition attributeDefinition)
            {
                result.Add(attributeDefinition);
            }
        }

        return result;
    }

    /// <summary>The attribute references on a block reference, skipping erased ones.</summary>
    public static List<AttributeReference> GetAttributeReferences(
        Transaction tr,
        BlockReference reference,
        OpenMode openMode)
    {
        List<AttributeReference> result = new();

        foreach (ObjectId id in reference.AttributeCollection)
        {
            if (id.IsErased)
            {
                continue;
            }

            if (tr.GetObject(id, openMode) is AttributeReference attribute)
            {
                result.Add(attribute);
            }
        }

        return result;
    }

    /// <summary>
    /// Every reference to a block, including the references to a dynamic block's anonymous
    /// variants — those point at a "*U" record, not at the named definition, and are missed by
    /// GetBlockReferenceIds on the named one alone.
    /// </summary>
    public static List<ObjectId> GetAllReferenceIds(Transaction tr, BlockTableRecord definition)
    {
        List<ObjectId> result = new();

        foreach (ObjectId id in definition.GetBlockReferenceIds(true, false))
        {
            result.Add(id);
        }

        if (definition.IsDynamicBlock)
        {
            foreach (ObjectId anonymousId in definition.GetAnonymousBlockIds())
            {
                BlockTableRecord anonymous = (BlockTableRecord)tr.GetObject(anonymousId, OpenMode.ForRead);
                foreach (ObjectId id in anonymous.GetBlockReferenceIds(true, false))
                {
                    result.Add(id);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// True when <paramref name="candidateId"/> is <paramref name="targetId"/> or nests a
    /// reference to it at any depth. Used to refuse a redefinition that would make a block
    /// contain itself.
    /// </summary>
    public static bool ReferencesBlock(
        Transaction tr,
        ObjectId candidateId,
        ObjectId targetId,
        HashSet<ObjectId> visited)
    {
        if (candidateId == targetId)
        {
            return true;
        }

        if (!visited.Add(candidateId))
        {
            return false;
        }

        BlockTableRecord candidate = (BlockTableRecord)tr.GetObject(candidateId, OpenMode.ForRead);
        foreach (ObjectId id in candidate)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is BlockReference nested
                && (ReferencesBlock(tr, nested.BlockTableRecord, targetId, visited)
                    || (nested.IsDynamicBlock && ReferencesBlock(tr, nested.DynamicBlockTableRecord, targetId, visited))))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Deep-clones the source entities into a block definition and shifts each clone by
    /// minus the base point.
    ///
    /// THE block-creation bug this exists to avoid: entities inside a definition are stored
    /// relative to the block's own origin, and AutoCAD does NOT apply BlockTableRecord.Origin
    /// as an offset to them. Cloning world-space geometry in unshifted puts the base point at
    /// the world origin, so every insertion lands displaced by the base point. The definition's
    /// Origin is left at (0,0,0) and the geometry is translated instead.
    ///
    /// DeepCloneObjects rather than a shallow clone so nested block references keep their
    /// attributes (same reasoning as TransformApplier.ApplyAsCopy). All sources must share one
    /// owner; the caller checks that.
    /// </summary>
    public static int CloneIntoDefinition(
        Transaction tr,
        Database db,
        IReadOnlyList<Entity> sources,
        BlockTableRecord definition,
        Point3d du_Base)
    {
        if (sources.Count == 0)
        {
            return 0;
        }

        ObjectIdCollection sourceIds = new();
        foreach (Entity source in sources)
        {
            sourceIds.Add(source.ObjectId);
        }

        IdMapping mapping = new();
        db.DeepCloneObjects(sourceIds, definition.ObjectId, mapping, false);

        Matrix3d toBlockSpace = Matrix3d.Displacement(du_Base.GetVectorTo(Point3d.Origin));

        foreach (Entity source in sources)
        {
            IdPair pair = mapping[source.ObjectId];
            Entity clone = (Entity)tr.GetObject(pair.Value, OpenMode.ForWrite);
            clone.TransformBy(toBlockSpace);
        }

        return sources.Count;
    }

    /// <summary>
    /// Appends attribute definitions, positioned in block space the same way the cloned
    /// geometry is (world position minus base point). Layer 0 so the attributes follow the
    /// reference's layer, which is the AutoCAD convention for block content.
    /// </summary>
    public static List<string> AddAttributeDefinitions(
        Transaction tr,
        AcadContext acad,
        BlockTableRecord definition,
        IReadOnlyList<AttributeSpec> specs,
        Mm_Point mm_Base)
    {
        List<string> tags = new(specs.Count);

        foreach (AttributeSpec spec in specs)
        {
            AttributeDefinition attributeDefinition = new();
            attributeDefinition.SetDatabaseDefaults(acad.Db);

            attributeDefinition.Tag = spec.Tag;
            attributeDefinition.Prompt = spec.Prompt;
            attributeDefinition.TextString = spec.DefaultValue;
            attributeDefinition.Height = acad.ToDu(spec.Mm_Height);
            attributeDefinition.Constant = spec.IsConstant;
            attributeDefinition.Invisible = spec.IsInvisible;
            attributeDefinition.Layer = "0";

            Mm_Point mm_BlockPosition = new(
                spec.Mm_Position.Mm_X - mm_Base.Mm_X,
                spec.Mm_Position.Mm_Y - mm_Base.Mm_Y,
                spec.Mm_Position.Mm_Z - mm_Base.Mm_Z);

            // Same AlignmentPoint trap as DBText: AttributeDefinition derives from it.
            TextTools.ApplyJustification(acad, attributeDefinition, spec.AttachmentPoint, mm_BlockPosition);

            definition.AppendEntity(attributeDefinition);
            tr.AddNewlyCreatedDBObject(attributeDefinition, true);

            // After the append, and only for justified text — see TextTools.AdjustIfJustified.
            TextTools.AdjustIfJustified(attributeDefinition, acad.Db);

            tags.Add(spec.Tag);
        }

        return tags;
    }

    /// <summary>The planner's view of a definition's attributes.</summary>
    public static List<AttributeSlot> ToSlots(IReadOnlyList<AttributeDefinition> definitions)
    {
        List<AttributeSlot> slots = new(definitions.Count);
        foreach (AttributeDefinition definition in definitions)
        {
            slots.Add(new AttributeSlot(definition.Tag, definition.Constant, definition.IsMTextAttributeDefinition));
        }

        return slots;
    }

    /// <summary>
    /// Inserts a reference and builds its attributes. Attributes are NOT automatic: a bare
    /// BlockReference has none, so each non-constant definition gets an AttributeReference via
    /// SetAttributeFromBlock with the reference's BlockTransform (the 2-parameter overload —
    /// it is what places the attribute in the reference's position, rotation and scale).
    /// Constant attributes are skipped: they display from the definition itself.
    ///
    /// <paramref name="valuesBySlot"/> is parallel to the definitions; null means "use the
    /// default". It must already have been validated by AttributeAssignment.TryPlan.
    /// </summary>
    public static BlockReference InsertReference(
        Transaction tr,
        Database db,
        BlockTableRecord space,
        BlockTableRecord definition,
        IReadOnlyList<AttributeDefinition> attributeDefinitions,
        IReadOnlyList<string?> valuesBySlot,
        Point3d du_Position,
        double rad_Rotation,
        Scale3d scale,
        string layer)
    {
        BlockReference reference = new(du_Position, definition.ObjectId);
        reference.SetDatabaseDefaults(db);
        reference.Rotation = rad_Rotation;
        reference.ScaleFactors = scale;
        reference.Layer = layer;

        space.AppendEntity(reference);
        tr.AddNewlyCreatedDBObject(reference, true);

        for (int i = 0; i < attributeDefinitions.Count; i++)
        {
            AttributeDefinition attributeDefinition = attributeDefinitions[i];
            if (attributeDefinition.Constant)
            {
                continue;
            }

            AppendAttribute(
                tr: tr,
                db: db,
                reference: reference,
                attributeDefinition: attributeDefinition,
                value: valuesBySlot[i] ?? attributeDefinition.TextString);
        }

        return reference;
    }

    /// <summary>
    /// The ATTSYNC equivalent for one reference: rebuild every attribute from the current
    /// definition (so position, height and style follow it), carrying values over by tag per
    /// <see cref="AttributeSyncPlan"/>. The existing attribute ids are snapshotted before any
    /// is erased — mutating a collection while enumerating it is undefined.
    /// </summary>
    public static ReferenceSyncResult SyncReference(
        Transaction tr,
        Database db,
        BlockReference reference,
        bool preserveValues)
    {
        BlockTableRecord definition = (BlockTableRecord)tr.GetObject(reference.BlockTableRecord, OpenMode.ForRead);

        List<AttributeDefinition> settable = new();
        foreach (AttributeDefinition attributeDefinition in GetAttributeDefinitions(tr, definition))
        {
            if (!attributeDefinition.Constant)
            {
                settable.Add(attributeDefinition);
            }
        }

        List<AttributeReference> current = GetAttributeReferences(tr, reference, OpenMode.ForWrite);

        List<AttributeDefault> defaults = new(settable.Count);
        foreach (AttributeDefinition attributeDefinition in settable)
        {
            defaults.Add(new AttributeDefault(attributeDefinition.Tag, attributeDefinition.TextString));
        }

        List<ExistingAttribute> existing = new(current.Count);
        foreach (AttributeReference attribute in current)
        {
            existing.Add(new ExistingAttribute(attribute.Tag, attribute.TextString));
        }

        AttributeSyncPlan plan = AttributeSyncPlan.Build(defaults, existing, preserveValues);

        foreach (AttributeReference attribute in current)
        {
            attribute.Erase();
        }

        foreach (SyncedAttribute synced in plan.Attributes)
        {
            AppendAttribute(
                tr: tr,
                db: db,
                reference: reference,
                attributeDefinition: settable[synced.DefinitionIndex],
                value: synced.Value);
        }

        reference.RecordGraphicsModified(true);

        return new ReferenceSyncResult(plan.PreservedCount, plan.ResetCount, plan.AddedTags, plan.RemovedTags);
    }

    /// <summary>
    /// Writes a value to an existing attribute. A multiline attribute keeps its text in a
    /// nested MText that only UpdateMTextAttribute() refreshes; a justified single-line one
    /// needs AdjustAlignment to recompute its position for the new string.
    /// </summary>
    public static void SetAttributeValue(Database db, AttributeReference attribute, string value)
    {
        attribute.TextString = value;

        if (attribute.IsMTextAttribute)
        {
            attribute.UpdateMTextAttribute();
            return;
        }

        TextTools.AdjustIfJustified(attribute, db);
    }

    /// <summary>Renders a reference's attributes for a payload.</summary>
    public static JsonArray DescribeAttributes(IReadOnlyList<AttributeReference> attributes)
    {
        JsonArray result = new();
        foreach (AttributeReference attribute in attributes)
        {
            result.Add(new JsonObject
            {
                ["tag"] = attribute.Tag,
                ["value"] = attribute.TextString,
                ["handle"] = attribute.Handle.ToString(),
                ["is_invisible"] = attribute.Invisible,
                ["is_multiline"] = attribute.IsMTextAttribute,
            });
        }

        return result;
    }

    private static void AppendAttribute(
        Transaction tr,
        Database db,
        BlockReference reference,
        AttributeDefinition attributeDefinition,
        string value)
    {
        AttributeReference attribute = new();
        attribute.SetAttributeFromBlock(attributeDefinition, reference.BlockTransform);
        attribute.TextString = value;

        reference.AttributeCollection.AppendAttribute(attribute);
        tr.AddNewlyCreatedDBObject(attribute, true);

        SetAttributeValue(db, attribute, value);
    }
}
