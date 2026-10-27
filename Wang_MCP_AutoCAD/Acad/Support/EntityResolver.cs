using System.Globalization;
using System.Text.Json.Nodes;
using Autodesk.AutoCAD.DatabaseServices;
using Wang_MCP_AutoCAD.Mcp;

namespace Wang_MCP_AutoCAD.Acad.Support;

/// <summary>
/// Turning the strings a caller sends into things the database can act on: handles into
/// ObjectIds, a space name into a BlockTableRecord, a layer name into a validated layer.
/// </summary>
internal static class EntityResolver
{
    /// <summary>
    /// The spaces a tool may target. Only model space for now; "paper" joins this list when
    /// layout support lands, and every tool's schema picks the new value up automatically
    /// because they all build their enum from here.
    ///
    /// "current" is deliberately absent and should stay that way: resolving via
    /// db.CurrentSpaceId would make a tool's effect depend on invisible UI state, which is
    /// the wrong property for an API driven by a model that cannot see the screen.
    /// </summary>
    public static IReadOnlyList<string> AllowedSpaces { get; } = new[] { "model" };

    public const string DefaultSpace = "model";

    /// <summary>
    /// Resolves the target block table record for a space key. A null key means model space.
    /// </summary>
    public static bool TryResolveSpace(
        Transaction tr,
        Database db,
        string? spaceKey,
        OpenMode openMode,
        out BlockTableRecord space,
        out string error)
    {
        space = null!;
        error = "";

        string key = string.IsNullOrWhiteSpace(spaceKey) ? DefaultSpace : spaceKey.Trim();

        if (!string.Equals(key, "model", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Unknown space \"{spaceKey}\". This server currently draws only into "
                  + $"model space; supported values are: {string.Join(", ", AllowedSpaces)}.";
            return false;
        }

        BlockTable blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        space = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], openMode);
        return true;
    }

    /// <summary>
    /// Resolves handle strings to entities.
    ///
    /// <paramref name="requireAll"/> should be true for every MUTATING tool and false for
    /// every read-only one. A partially applied transform across twenty entities leaves the
    /// drawing in a state the caller cannot reason about and cannot cleanly undo, so a
    /// mutation that cannot resolve every handle changes nothing at all. A partial read is
    /// harmless and saves a round trip.
    ///
    /// <paramref name="failures"/> is always populated so the caller does not have to parse
    /// prose to find out which handles were bad.
    /// </summary>
    public static bool TryResolveHandles(
        Transaction tr,
        Database db,
        IReadOnlyList<string> handles,
        OpenMode openMode,
        bool requireAll,
        out List<Entity> entities,
        out JsonArray failures,
        out string error)
    {
        entities = new List<Entity>();
        failures = new JsonArray();
        error = "";

        foreach (string raw in handles)
        {
            if (!HandleSyntax.TryNormalize(raw, out string normalized, out string syntaxError))
            {
                AddFailure(failures, raw, syntaxError);
                continue;
            }

            if (!TryOpenEntity(tr, db, normalized, openMode, out Entity? entity, out string openError))
            {
                AddFailure(failures, raw, openError);
                continue;
            }

            entities.Add(entity!);
        }

        if (requireAll && failures.Count > 0)
        {
            error = DescribeAllOrNothingFailure(handles.Count, failures);
            entities.Clear();
            return false;
        }

        if (entities.Count == 0 && handles.Count > 0)
        {
            error = "None of the supplied handles could be resolved. "
                  + "Call acad_list_entities to refresh your handles.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Rejects a handle list naming the same entity twice. Without this, an in-place transform
    /// applies twice to that entity — a move goes twice as far — a copy makes two clones, and
    /// a block definition gets the entity twice.
    /// </summary>
    public static bool TryRejectDuplicates(IReadOnlyList<Entity> entities, out string error)
    {
        error = "";
        HashSet<ObjectId> seen = new();
        List<string> duplicates = new();

        foreach (Entity entity in entities)
        {
            if (!seen.Add(entity.ObjectId))
            {
                duplicates.Add(entity.Handle.ToString());
            }
        }

        if (duplicates.Count == 0)
        {
            return true;
        }

        error = $"These handles appear more than once, so nothing was changed: "
              + $"{string.Join(", ", duplicates)}. Pass each handle once.";
        return false;
    }

    private static bool TryOpenEntity(
        Transaction tr,
        Database db,
        string normalizedHandle,
        OpenMode openMode,
        out Entity? entity,
        out string error)
    {
        entity = null;
        error = "";

        long handleValue;
        try
        {
            handleValue = long.Parse(normalizedHandle, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        }
        catch (System.Exception)
        {
            error = "is not a value this drawing can interpret as a handle";
            return false;
        }

        Handle handle = new(handleValue);

        if (!db.TryGetObjectId(handle, out ObjectId id) || id.IsNull)
        {
            error = "no object with this handle exists in this drawing";
            return false;
        }

        if (id.IsErased)
        {
            error = "the object with this handle has been erased";
            return false;
        }

        // A handle can name a layer record or a dictionary just as well as an entity; letting
        // one through produces a confusing cast exception much further downstream.
        DBObject dbObject = tr.GetObject(id, openMode);
        if (dbObject is not Entity resolved)
        {
            error = $"this handle names a {dbObject.GetType().Name}, not a drawable entity";
            return false;
        }

        entity = resolved;
        return true;
    }

    private static void AddFailure(JsonArray failures, string handle, string reason)
    {
        failures.Add(new JsonObject
        {
            ["handle"] = handle,
            ["reason"] = reason,
        });
    }

    private static string DescribeAllOrNothingFailure(int requested, JsonArray failures)
    {
        List<string> parts = new();
        foreach (JsonNode? node in failures)
        {
            if (node is JsonObject failure)
            {
                parts.Add($"{failure["handle"]} ({failure["reason"]})");
            }
        }

        return $"{failures.Count} of {requested} handles could not be resolved, so nothing was "
             + $"changed. Failures: {string.Join(", ", parts)}. Call acad_list_entities to "
             + "refresh your handles.";
    }

    /// <summary>
    /// Resolves an optional layer name to a layer that exists. A null name means "use the
    /// drawing's current layer" and always succeeds.
    ///
    /// An unknown layer is an error, not an invitation to create one: a typo must not
    /// silently spawn a junk layer that only shows up later in the drawing.
    /// </summary>
    public static bool TryResolveLayer(
        Transaction tr,
        Database db,
        string? layerName,
        out string resolvedLayer,
        out string error)
    {
        resolvedLayer = "";
        error = "";

        LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

        if (layerName is null)
        {
            LayerTableRecord current = (LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead);
            resolvedLayer = current.Name;
            return true;
        }

        if (!layerTable.Has(layerName))
        {
            error = $"Layer \"{layerName}\" does not exist in this drawing. Create it with "
                  + "acad_create_layer, or omit the layer argument to use the current layer.";
            return false;
        }

        resolvedLayer = layerName;
        return true;
    }

    /// <summary>
    /// True when the named layer is locked. Entities on a locked layer cannot be opened for
    /// write, and catching it here names the layer instead of surfacing eOnLockedLayer.
    /// </summary>
    public static bool IsLayerLocked(Transaction tr, Database db, string layerName)
    {
        LayerTable layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

        if (!layerTable.Has(layerName))
        {
            return false;
        }

        LayerTableRecord record = (LayerTableRecord)tr.GetObject(layerTable[layerName], OpenMode.ForRead);
        return record.IsLocked;
    }
}
