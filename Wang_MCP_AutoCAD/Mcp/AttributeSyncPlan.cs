namespace Wang_MCP_AutoCAD.Mcp;

/// <summary>A non-constant attribute definition, as the sync planner sees it.</summary>
public readonly record struct AttributeDefault(string Tag, string DefaultValue);

/// <summary>An attribute currently on a reference.</summary>
public readonly record struct ExistingAttribute(string Tag, string Value);

/// <summary>One attribute the synced reference will carry.</summary>
public readonly record struct SyncedAttribute(int DefinitionIndex, string Value, bool Preserved);

/// <summary>
/// The ATTSYNC equivalent's decision: given a block definition's attributes and what one
/// reference currently carries, what the reference should carry afterwards.
///
/// The detail that matters most in all of Phase 4 lives here: values are preserved BY TAG.
/// A sync that resets every reference to the definition defaults is silent data loss across
/// every insertion of the block — the drawing still looks plausible, and every title block
/// now says "A1". So <c>preserveValues</c> defaults to true at the tool, and a matched tag
/// keeps its value. Matching is case-insensitive; duplicate tags pair up in order.
///
/// Even a matched attribute is rebuilt by the Acad side (SetAttributeFromBlock) so it picks
/// up the definition's new position, height and style — only the value carries over.
/// </summary>
public sealed class AttributeSyncPlan
{
    private AttributeSyncPlan(
        IReadOnlyList<SyncedAttribute> attributes,
        IReadOnlyList<string> addedTags,
        IReadOnlyList<string> removedTags,
        int preservedCount,
        int resetCount)
    {
        Attributes = attributes;
        AddedTags = addedTags;
        RemovedTags = removedTags;
        PreservedCount = preservedCount;
        ResetCount = resetCount;
    }

    /// <summary>In definition order: one entry per non-constant definition attribute.</summary>
    public IReadOnlyList<SyncedAttribute> Attributes { get; }

    /// <summary>Tags the definition has that the reference did not; they get the default.</summary>
    public IReadOnlyList<string> AddedTags { get; }

    /// <summary>Tags the reference had that the definition no longer has; they are removed.</summary>
    public IReadOnlyList<string> RemovedTags { get; }

    /// <summary>Matched attributes that kept their existing value.</summary>
    public int PreservedCount { get; }

    /// <summary>Matched attributes reset to the default because preservation was off.</summary>
    public int ResetCount { get; }

    public static AttributeSyncPlan Build(
        IReadOnlyList<AttributeDefault> definition,
        IReadOnlyList<ExistingAttribute> existing,
        bool preserveValues)
    {
        Dictionary<string, Queue<string>> existingByTag = new(StringComparer.OrdinalIgnoreCase);
        foreach (ExistingAttribute attribute in existing)
        {
            if (!existingByTag.TryGetValue(attribute.Tag, out Queue<string>? queue))
            {
                queue = new Queue<string>();
                existingByTag[attribute.Tag] = queue;
            }

            queue.Enqueue(attribute.Value);
        }

        List<SyncedAttribute> attributes = new(definition.Count);
        List<string> added = new();
        int preserved = 0;
        int reset = 0;

        for (int i = 0; i < definition.Count; i++)
        {
            AttributeDefault slot = definition[i];

            if (existingByTag.TryGetValue(slot.Tag, out Queue<string>? queue) && queue.Count > 0)
            {
                string current = queue.Dequeue();

                if (preserveValues)
                {
                    attributes.Add(new SyncedAttribute(i, current, Preserved: true));
                    preserved++;
                }
                else
                {
                    attributes.Add(new SyncedAttribute(i, slot.DefaultValue, Preserved: false));
                    reset++;
                }

                continue;
            }

            attributes.Add(new SyncedAttribute(i, slot.DefaultValue, Preserved: false));
            added.Add(slot.Tag);
        }

        List<string> removed = new();
        foreach (KeyValuePair<string, Queue<string>> pair in existingByTag)
        {
            for (int n = 0; n < pair.Value.Count; n++)
            {
                removed.Add(pair.Key);
            }
        }

        return new AttributeSyncPlan(attributes, added, removed, preserved, reset);
    }
}
