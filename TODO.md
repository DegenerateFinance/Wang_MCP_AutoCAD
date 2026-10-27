# TODO — expanding the MCP tool surface

Tracking the build-out from 2 drawing tools to ~27. Phases are ordered so each one is
independently buildable, testable and demoable.

Full reasoning, per-tool schemas and the API verification behind all of this live in the plan
at `~/.claude/plans/now-that-we-have-noble-quail.md`.

**Status:** Phases 0–4 complete, 31 tools. Live-verified: 2, 3, 3b and 4. Not yet
live-verified: 1 (no brief).
Tests: 85 → 493. Live briefs live in `tests/live/`, results in `tests/results/`; and are copied to the deploy
folder by `tests/deploy-briefs.ps1`.

---

## Decisions already taken (do not re-litigate)

| Question | Decision |
|---|---|
| How do edit tools select objects? | **Handles only.** Caller queries first, then acts. No filter shorthand, no `Editor.SelectImplied` — Editor selection is unreliable from application context. |
| Unknown layer | **Stay strict.** Drawing tools fail; `acad_create_layer` is the explicit fix. No auto-create, no opt-in flag — a typo must not silently spawn a junk layer. |
| Model vs paper space | **Model only for now**, but every creation tool already takes a `space` argument whose enum has one value, so adding `"paper"` later changes `EntityResolver` and nothing else. |
| Five transforms or one? | **One `acad_transform` with a `mode` enum.** They are all "build a Matrix3d, apply it"; five near-identical handlers would be the largest duplication in the codebase and five sibling tools hurt model tool-selection. |
| Tool-count bloat | **Tool groups**, already implemented. A session advertises a subset; effective count drops to 10–15 without losing capability. |

---

## Verified AutoCAD 2026 API facts

Confirmed by reading `acdbmgd.dll` / `accoremgd.dll` metadata (assembly `25.1.0.0`) directly,
not from documentation.

- **Editing a block definition IS possible.** `BlockTableRecord` has `AppendEntity`,
  `AssumeOwnershipOf`, `GetBlockReferenceIds`, `UpdateAnonymousBlocks`. Open it `ForWrite`,
  swap contents, commit — existing references update automatically.
- **Attribute changes do NOT propagate** to existing references. That is the real limitation
  behind the "you can't edit block definitions" folklore, and it needs a manual ATTSYNC
  equivalent.
- **Dynamic block *authoring* is NOT possible** — verified by absence. No `BlockParameter`,
  `BlockAction`, `DynBlockTableRecord` or related types exist in the managed API; the
  parameter/action graph is ObjectARX C++ only. Existing dynamic property *values* are
  readable and writable. Consequence: `acad_redefine_block` must **refuse** a dynamic block.
- **No managed undo-mark API.** `Document` has no `Undo`; the only undo surface anywhere is
  `TransactionManager.DisableUndoRecording`, which suppresses rather than groups. See Open
  questions.
- Angles are **radians** in the database; the wire carries degrees. Area scales by the
  **square** of the unit factor. `Transparency` is an **alpha byte** where 255 is opaque.
  `LineWeight` and `BlockScaling` are **enums**, not free numbers.

---

## Phase 0 — infrastructure ✅ DONE

- [x] `ToolArgs`: `TryGetString`, `TryGetBoolOrDefault`, `TryGetIntRequired`,
      `TryGetNonZeroDouble`, `TryGetEnum`, `TryGetStringArray`, `TryGetDoubleArray`,
      `TryGetArrayElement`, `TryGetObjectElement`, `HasArgument`, `RequireExactlyOneOf`,
      `RequireAtLeastOneOf`
- [x] `ToolSchema`: `Boolean`, `StringEnum`, `RequiredInteger`, `NumberInRange`,
      `StringArray`, `PointMm`, `VertexArrayMm`, `StringMap`
- [x] `Units`: `DegToRad` / `RadToDeg` / `NormalizeDeg`, area conversion that squares the
      factor, transparency percent ↔ alpha
- [x] `Mm_Point` / `Mm_Vertex`, `PointArgs`, `HandleSyntax`, `TransformRequest`,
      `LineWeights`, `MTextEscaper`, `SymbolNaming` / `LayerNaming` / `BlockNaming`
- [x] `Acad/Support/`: `AcadContext`, `EntityResolver`, `EntityDescriber`, `TransformApplier`
- [x] Tool groups on `McpTool` + `ToolRegistry` filtering
- [x] `AcadTools.RegisterAll` as the single wiring point

---

## Phase 1 — layers and properties ✅ DONE

- [x] `acad_list_layers`
- [x] `acad_create_layer` — `if_exists` = error / ignore / update
- [x] `acad_set_layer_state` — freeze, lock, off, plottable, colour, lineweight, description,
      transparency
- [x] `acad_set_current_layer`
- [x] `acad_get_entity_properties` — best-effort handle resolution
- [x] `acad_set_entity_properties` — includes changing an object's layer; all-or-nothing
- [x] `acad_delete_entities` — optional `confirm_count` guard

---

## Phase 2 — geometry and text ✅ DONE

- [x] `acad_draw_line` — rejects zero-length
- [x] `acad_draw_arc` — degrees in, radians in the DB; documents the CCW convention
- [x] `acad_draw_polyline` — rejects a per-vertex `mm_z`; sets `Elevation`/`ConstantWidth`
      after the vertices
- [x] `acad_draw_rectangle` — closed polyline convenience
- [x] `acad_add_text` — **the `AlignmentPoint` trap**: for any justification other than Left,
      AutoCAD ignores `Position`. Set `TextString`/`Height`/`Rotation`/`Justify`, then BOTH
      `Position` and `AlignmentPoint`, then `AdjustAlignment(db)`. Also report
      `height_overridden_by_style` when the style has a fixed height.
- [x] `acad_add_mtext` — `escape_formatting` defaulting true; `mm_width: 0` means no wrap;
      read `ActualWidth`/`ActualHeight` after append, before commit
- [x] `acad_set_text_content` — DBText / MText / AttributeReference; call
      `UpdateMTextAttribute()` after changing an MText attribute

### Phase 2 notes worth keeping

- `DBText.Justify` and `MText.Attachment` are **both** `AttachmentPoint` (verified off
  `acdbmgd.dll` metadata, 25.1.0.0) — so one mapping table serves both tools. It lives in
  `Mcp/TextJustification.cs`, Autodesk-free so it is unit tested; 33 tests pin the wire
  name → enum integer contract, since the test project never gets the AutoCAD DLLs.
- **12 of the 24 `AttachmentPoint` values are exposed.** Align and Fit (`BaseAlign`,
  `BaseFit`, …) need a *second* point and ignore the height, so offering them alongside a
  single `mm_position` and an `mm_height` that silently stops applying would be worse than
  not offering them. The `Mid` family duplicates Center closely enough to hurt tool
  selection. `DescribeAttachmentPoint` still *names* all 24, because a read must not fail on
  text someone else drew.
- `AdjustAlignment` takes a `Database` argument (`AdjustAlignment(db)`, not `()`), and
  `MText.ActualWidth`/`ActualHeight` are **read-only** — both confirmed from metadata.
- `AttributeReference` must be matched **before** `DBText` in any `switch` (it derives from
  it), or the `UpdateMTextAttribute()` call for a multiline attribute is skipped and the
  drawing keeps displaying the old value. `AttributeDefinition` is deliberately *rejected* by
  `acad_set_text_content`: it lives in a block definition, so rewriting it changes no existing
  reference and the tool's effect would be invisible.
- `acad_set_text_content` validates the whole handle set **before** mutating any of it, so a
  non-text handle halfway down the list cannot leave the first half already rewritten.

---

## Phase 3 — transforms and copy ✅ DONE (live-verified except attributed copy)

- [x] `acad_transform` — `mode` = move / rotate / scale / mirror, plus `copy`
  - `Matrix3d.Rotation` needs `Vector3d.ZAxis`; the WCS X axis silently rotates out of plane
  - **Mirrored text reads backwards** — `TransformBy` does not consult `MIRRTEXT`. Counted
    and reported as `mirrored_text_count`, never silently "fixed". A **negative scale factor**
    flips handedness too, so it is counted as well (`TransformRequest.IsReflection`)
- [x] `acad_copy` — displacement plus a `count`; copy N sits at N × displacement. A *linear*
      row, not a rectangular grid — rows × columns stays in Phase 6's array item

### Phase 3 notes worth keeping

- **Copies use `DeepCloneObjects`, reversing the plan's `GetTransformedCopy`.**
  `GetTransformedCopy` is a shallow `Clone()`, which does not carry owned sub-objects. An
  attributed `BlockReference` owns its `AttributeReference`s, so a shallow copy is expected to
  arrive without them. That is from API reasoning, not verified live — round 5 of
  `tests/live/PHASE3_TRANSFORMS.md` is the check. `DeepCloneObjects` needs one owner per call,
  so `TransformApplier.ApplyAsCopy` groups by `OwnerId` and maps back through `IdMapping` to
  keep `new_handles` in input order.
- **Arguments that belong to a different mode are rejected, not ignored** (`TransformArgs`).
  A flat schema shared by four modes otherwise lets `deg_angle` ride along with
  `mode: "scale"` and look like it worked. The schema is built in the same class, and a test
  pins it to exactly what the reader consumes.
- **Duplicate handles are rejected.** Without that check, an in-place move applies twice to
  the repeated entity. The other mutators don't guard against this yet; it's harmless for
  set-properties and delete, but worth adding wherever an operation is not idempotent.
- `acad_copy` with a zero displacement is an error (an invisible stacked duplicate), whereas
  a zero-length move is a no-op.
- Copies open their sources `ForRead`, but the clone has to be opened `ForWrite` to displace
  it. Copying from a locked layer is *predicted* to fail with `eOnLockedLayer`. That isn't
  verifiable from metadata.
- An `AttributeReference` cannot be copied on its own (its owner is not a block table
  record); the tool says to copy the owning block reference instead.

---

## Phase 3b — several open drawings ✅ DONE (live-verified)

- [x] Every `AcadContext.NewPayload()` response names its drawing: `drawing`,
      `drawing_path`, `drawing_is_active`. `acad_draw_circle` and `acad_list_entities` were
      moved onto `AcadContext` to get this; they also used to read `MdiActiveDocument` directly
- [x] `acad_list_drawings` — in the `core` group so it is advertised however a session
      filters groups; `RequiresDocument = false` so it works with nothing open; flags
      `ambiguous_name` when two open drawings share a file name
- [x] Optional `drawing` argument on every document tool, injected by `ToolRegistry.Add`
      (no tool can forget it). Matched by full path, then file name with or without `.dwg`;
      a shared file name is **refused as ambiguous**, never resolved to the first match
      (`DrawingSelector`, unit tested)
- [x] **Settled live (`tests/results/PHASE3B_DRAWINGS_RESULTS.md`): a running MCPSTART can READ
      a non-active drawing but a WRITE fails with `eWrongDatabase`.** The
      `CommandFlags.Session` fallback was **not** taken. Rule instead: **writes edit the
      active drawing only.** `McpTool.IsReadOnly` (default `false`, so a forgetful new tool
      gets the strict rule) marks the three reads — `acad_list_entities`,
      `acad_get_entity_properties`, `acad_list_layers`. On write tools `drawing` is a
      **guard**: naming any other drawing is refused before AutoCAD is touched, with a message
      naming the active drawing, logged `outcome=refused_not_active`
- [x] Retest passed live: guard match, clean refusal, reads of the non-active drawing, handle
      collision demonstrated, guard after a tab switch
- [ ] **Live question:** what happens when the drawing MCPSTART was run from is closed?
- [ ] Ambiguous file names (two open drawings named the same) — unverified live
- Deliberately **no** "activate drawing" tool: which drawing is active is the user's choice.

---

## Phase 4 — blocks ✅ DONE (live-verified, Retest 2 in `tests/results/PHASE4_BLOCKS_RESULTS.md`; justified attribute definitions still fail)

All 11 tools in `Acad/BlockTools.cs`, plumbing in `Acad/Support/BlockSupport.cs`, every
decision that does not need AutoCAD in `Mcp/` (`BlockRules`, `AttributeSpecReader`,
`AttributeAssignment`, `AttributeSyncPlan`, `InsertScale`, `DynamicPropertyConverter`).

- [x] `acad_list_blocks` (read) — layouts never; anonymous and xrefs opt-in; unloaded xrefs never
- [x] `acad_get_block_definition` (read) — attribute definitions with block-space positions
- [x] `acad_create_block` — clones translated by **minus the base point**, `Origin` left at 0.
      `source_action` replace (default) / keep / erase. Takes new **attribute definitions**
      (`attributes`), since nothing else can author one — the acceptance test needs it
- [x] `acad_insert_block` — attributes built per definition with the 2-parameter
      `SetAttributeFromBlock`; constants skipped and reported; `BlockScaling.Uniform` pre-checked
- [x] `acad_get_block_attributes` (read) — includes definition constants, and lists definition
      tags missing from a reference (a hint to sync)
- [x] `acad_set_block_attributes` — all references planned before any is written
- [x] `acad_redefine_block` — old contents snapshotted before erase; refuses dynamic, xref,
      layout, anonymous and self-containing redefinitions; syncs references afterwards,
      preserving values by default; calls `Editor.Regen()` after commit
- [x] `acad_sync_block_attributes` — rebuilds every attribute (so position/height/style follow
      the definition) and restores values **by tag**; refreshes dynamic blocks' anonymous
      variants first (`UpdateAnonymousBlocks`)
- [x] `acad_get/set_dynamic_block_properties` — value type taken from the boxed type of the
      current `Value` (double/int/short/string), not `PropertyTypeCode`, so no `DwgDataType`
      integers are assumed; units by `UnitsType` name; allowed-value lists enforced and snapped
- [x] `acad_convert_block_to_static` — single handle plus a required `new_block_name`, since
      every conversion creates its own block

### Phase 4 notes worth keeping

- **Compiled first time against `acdbmgd.dll` 25.1**, which confirms these API names exist:
  `IdMapping[ObjectId]`, `BlockTableRecord.UpdateAnonymousBlocks/GetAnonymousBlockIds/
  IsUnloaded/IsFromOverlayReference`, `BlockReference.ConvertToStaticBlock(string)`,
  `DynamicBlockReferencePropertyUnitsType.Angular`.
- **A unit test caught a real bug before any live run:** `kind == Short ? (short)x : x` has
  type `int`, so a short dynamic property would have been assigned a boxed `int` — which
  AutoCAD rejects. The conditional was replaced with explicit boxing per branch.
- Attribute definitions go on layer 0 (the AutoCAD convention, so they follow the reference's
  layer) and use `TextTools.ApplyJustification`, since `AttributeDefinition` derives from
  `DBText` and has the same AlignmentPoint trap.
- Copy/create/redefine all deep-clone; the Phase 3 attributed-copy check moved to round 4 of
  the Phase 4 brief, since this phase can finally create an attributed block.
- **First live run (`tests/results/PHASE4_BLOCKS_RESULTS.md`):** blocks without attributes
  pass — base point applied exactly once, rotated + scaled insert correct, confirmed on screen.
  **Every create with `attributes` failed `eInvalidContext`**: `AdjustAlignment(db)` on an
  `AttributeDefinition` not yet in the database throws, the alternate-database argument
  notwithstanding. Moving it after the append did **not** fix it (run 2: same error, now from
  the post-append call). Current hypothesis: it consults `HostApplicationServices.
  WorkingDatabase`, which follows the drawing MCPSTART was started in, not the target.
  The gateway now switches the working database to the target for every call and restores
  it (logs `working_db_was_target`), and `AdjustAlignment` is skipped for left-justified text
  (a no-op there). Round 1b in the brief separates the two explanations. Rounds 2–8 still to run.
- **Open, dev loop:** in run 2 a `HOTLOAD` left `acad_ping` showing the old server's uptime —
  the old server apparently never stopped. Unexplained; the brief asks for a reproduction.
- **Open, live only:** does a redefinition repaint without `Regen` (the tool calls it anyway)?
  What are the real names, kinds and units of a palette dynamic block's properties?

---

## Phase 5 — live verification

- [ ] Split `MCP_TEST_BRIEF.md`: keep the front matter, move per-phase checks to
      `tests/live/PHASE{1..4}_*.md` — Phase 2 and 3 done (`PHASE2_TEXT.md`,
      `PHASE3_TRANSFORMS.md`), Phases 1/4 still to write
- [x] **Correct the existing brief** — done as a warning block at the top of
      `MCP_TEST_BRIEF.md` rather than a rewrite: flags that §5.5's 15-second timeout and
      §5.6's `gateway/abort` no longer exist, that the "differing thread ids" evidence is now
      inverted (inline loop ⇒ same thread), that the `C:\Hyper-Shared\ACAD_DLL` "stale"
      warning is backwards now `deploy.local.props` points there, and that three tools is now
      17. Sections 5.1–5.4 and 5.7 still stand.
- [ ] Add **round-trip checks** per phase: create with tool X, read back with
      `acad_get_entity_properties`, assert every value matches. Catches angle bugs
      mechanically instead of by eye
- [x] Phase 2 angle check — arc 0°→90° reads back 0 and 90 (`PHASE2_TEXT_RESULTS.md`)
- [x] **Phase 2 justification check** — `middle_center`/`top_right` are stored correctly; the
      view did not repaint them in place (display only)
- [x] Phase 2 style check — found that AutoCAD does NOT apply a style's fixed height to
      API-created text; `acad_add_text` now applies it itself. Verified live after the fix
- [ ] Phase 2 attribute check: `acad_set_text_content` against a **multiline** attribute, and
      confirm the displayed value updates (this is what `UpdateMTextAttribute()` is for — a
      single-line attribute would pass even if that call were missing)
- [x] **The Phase 4 acceptance test** — passed live (Retest 2): five references kept their own
      values through a redefine that added a second attribute
- [x] First Phase 4 live check: redefined references repainted without a manual `REGEN`
- [ ] Justified attribute definitions in `acad_create_block`/`acad_redefine_block` fail
      `eInvalidContext` in `DBText.AdjustAlignment`; left-justified pass (Retest 2, failure 2)
- [ ] `acad_get_dynamic_block_properties` still treats a reference as dynamic after
      `acad_convert_block_to_static` (Retest 2, round 7)
- [ ] `acad_create_layer` is the canary for whether `LockDocument()` alone suffices for
      symbol-table writes — everything so far only appends to model space

---

## Phase 6 — deferred (all verified available)

Real gaps in "drive a drawing end to end", but none block the surface above.

- [ ] Offset (`GetOffsetCurves`), trim (`GetSplitCurves`), `Extend`
- [ ] Rectangular / polar array
- [ ] Hatch (`AppendLoop`, `SetHatchPattern`, `EvaluateHatch`)
- [ ] Dimensions
- [ ] Save / save-as (`Database.SaveAs`)
- [ ] Zoom / regen (`Editor.Regen`)
- [ ] A `type` filter on `acad_list_entities` — cheap, and handles-only selection makes query
      quality matter more than it did

---

## Open questions

**Undo grouping — needs a decision.** Each tool commits its own transaction, so a 10-call
sequence leaves 10 undo steps and a failure at call 6 leaves the drawing half-built with no
way to roll back as a unit. There is no managed undo-mark API; the only grouping mechanism is
`SendStringToExecute("UNDO Begin")`, a command-string passthrough that `CLAUDE.md` explicitly
rules out. Options: accept and document it, or add
`acad_begin_undo_group`/`acad_end_undo_group` as the one sanctioned exception.
**Current recommendation: accept for now**, document per-call atomicity in the tool
descriptions, revisit if multi-step failures prove painful.

**Serialised listener.** Requests are handled inline on the document thread, so a bulk
transform over thousands of handles blocks every other request and the UI. Handle lists are
capped at 1000 to bound this; revisit with real timings rather than guessing twice.

**Uncommitted in the working tree:** a pre-existing `.csproj` fix changing `DLL\**` to
`AutoCAD_DLL\**` in the `Compile Remove` globs. Checked: the folder on disk really is
`AutoCAD_DLL\`, so the old globs excluded a directory that does not exist and the new ones
match reality. Correct fix — keep it. (It is cosmetic in practice: the folder holds only
`.dll`s, which the globs would never have compiled anyway.)
