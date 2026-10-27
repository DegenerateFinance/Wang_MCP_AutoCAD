# Phase 4 live check — blocks — RESULTS

Runs 1 and 2 are below as originally written. **Retest 2 (end of file) supersedes their
verdict.** Current verdict, from Retest 2:

| Group | Verdict |
|---|---|
| create / insert | **PASS** — base point, rotation, scale, attribute values, lowercase tag match, refusals |
| attributes (create, set/get, copy) | **PASS for left-justified attributes.** Justified attribute definitions (`justify` other than `left`) still **FAIL** `eInvalidContext` on the committed threading model |
| redefine / sync | **PASS** — acceptance test: every reference kept its own value; repainted without manual `REGEN` (see note on `regen`) |
| dynamic | **PASS** — read/set/refuse/convert; one reporting bug after convert |
| units (round 8) | **DROPPED** — non-mm drawings are out of scope |

## Superseded verdict (runs 1 and 2)

| Group | Verdict |
|---|---|
| create / insert (no attributes) | **PASS** — base point correct, insert with rotation + scale correct |
| attributes (create with attribute defs, set/get, copy) | **FAIL / BLOCKED** — `acad_create_block` with `attributes` always returns `InvalidContext` |
| redefine / sync | **NOT RUN** — needs attributed block (and probably shares the same failing path) |
| dynamic | **NOT RUN** |

## 2. Environment

- AutoCAD 2026, MCP server pid 12916, port 5599, `wang-mcp-autocad` 1.0.0, protocol 2025-06-18.
- Drawing3.dwg (fresh, INSUNITS 4, `mm_per_drawing_unit` 1). Drawing1 and Drawing2 were also open; every write used the `drawing: "Drawing3.dwg"` guard. Drawing2 was not used (it held 106 leftover entities).
- Log level **Info** (the brief asks for Debug; not changed). Log: `%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log`.
- DLL path/timestamp and AcadHotLoader vs direct NETLOAD: not checked by me.
- `tools/list` count (31 expected) not checked.

## 3. Per-round results

| Round | Result | Notes |
|---|---|---|
| 1 create attributed block | **FAIL** | see section 4 |
| 1 (variant, no attributes) | **PASS** | `P4_PLAIN`, base (1000,1000), source `2CD` replaced; `entity_count` 1, `erased_source_count` 1, `inserted_handle` `2DE`. `acad_get_block_definition`: polyline `mm_min` [0,0,0], `mm_max` [100,50,0] — base point applied once. `reference_count` 1 |
| 2 insert | **PARTIAL** | attribute values, lowercase tag match and the two refusals not run. Without attributes: `2DF` at (0,0); `2E0` at (500,0), 90 deg, scale 2 (`scale [2,2,2]`) |
| 3 set/get attributes | not run | |
| 4 copy attributed block | not run | |
| 5 redefine + sync (acceptance test) | not run | |
| 6 sync, self-reference, refusals | not run | `acad_list_blocks` seen: `P4_PLAIN` `has_attributes` false, `reference_count` 3 |
| 7 dynamic blocks | not run | |
| 8 units | not run | |

Visual confirmation by the user (screenshot): box at origin 100x50; rotated box about 100 wide and 200 tall,
left of x=500 and rising to y~200; box at (1000,1000) of 100x50. All match the API results.

## 4. Failures

`acad_create_block`, name `P4_BOX`, handles `["2CD"]`, base (1000,1000), attributes `TAG` (default A1, at 1010,1020, h10)
and constant `MAKER` (default WANG, at 1010,1005, h5). Sent three times (once with the rectangle selected in AutoCAD).
Each returned:

```
AutoCAD rejected the request to create the block: InvalidContext. Nothing was changed.
```

Drawing unchanged each time (rectangle `2CD` still there, no block). Log:

```
2026-10-27 17:18:16.485 ERROR [  5] op=tool/create_block name="P4_BOX" outcome=acad_error error_status=InvalidContext elapsed_ms=66
Autodesk.AutoCAD.Runtime.Exception: eInvalidContext
   at Autodesk.AutoCAD.Runtime.Interop.ThrowExceptionForErrorStatus(Int32 errorStatus)
   at Autodesk.AutoCAD.DatabaseServices.DBText.AdjustAlignment(Database alternateDatabaseToUse)
   at Wang_MCP_AutoCAD.Acad.TextTools.ApplyJustification(AcadContext acad, DBText dbText, Int32 attachmentPoint, Mm_Point mm_Position) in ...\Acad\TextTools.cs:line 261
   at Wang_MCP_AutoCAD.Acad.Support.BlockSupport.AddAttributeDefinitions(Transaction tr, AcadContext acad, BlockTableRecord definition, IReadOnlyList`1 specs, Mm_Point mm_Base) in ...\Acad\Support\BlockSupport.cs:line 244
   at Wang_MCP_AutoCAD.Acad.BlockTools.FillDefinition(...) in ...\Acad\BlockTools.cs:line 742
   at Wang_MCP_AutoCAD.Acad.BlockTools.CreateBlock(ToolArgs args) in ...\Acad\BlockTools.cs:line 383
2026-10-27 17:18:16.502 WARN  [  5] op=tools/call tool=acad_create_block outcome=tool_error elapsed_ms=91
```

### Retest after rebuilt DLL (17:36) — still FAIL, different stack

A first reload (HOTLOAD only) did not take effect: `acad_ping` showed the same pid 12916 and 42 min uptime, and the
probe failed identically. After a full AutoCAD restart the server came up as pid 18184 (uptime 18 s) with
`Y:\ACAD_DLL\Wang_MCP_AutoCAD.dll` dated 2026-10-27 17:27:40. Probe: `acad_create_block` name `P4_PROBE`, no handles,
base (0,-500), one attribute `TAG` (default A1, at 10,-480, h10), on Drawing3.dwg. Result: same `InvalidContext`,
nothing changed. Log:

```
2026-10-27 17:36:51.997 ERROR [ 12] op=tool/create_block name="P4_PROBE" outcome=acad_error error_status=InvalidContext elapsed_ms=18
Autodesk.AutoCAD.Runtime.Exception: eInvalidContext
   at Autodesk.AutoCAD.Runtime.Interop.ThrowExceptionForErrorStatus(Int32 errorStatus)
   at Wang_MCP_AutoCAD.Acad.Support.BlockSupport.AddAttributeDefinitions(...) in ...\Acad\Support\BlockSupport.cs:line 251
   at Wang_MCP_AutoCAD.Acad.BlockTools.FillDefinition(...) in ...\Acad\BlockTools.cs:line 742
   at Wang_MCP_AutoCAD.Acad.BlockTools.CreateBlock(ToolArgs args) in ...\Acad\BlockTools.cs:line 383
```

The `DBText.AdjustAlignment` / `TextTools.ApplyJustification` frames are gone, so the earlier fix changed the code path, but
`eInvalidContext` is now thrown directly from `AddAttributeDefinitions` line 251 (source not available here; the throwing
call is unknown). Drawing3 was not a fresh drawing for this retest (it held `P4_PLAIN` + 3 references from the earlier
run); the full round-by-round run was not restarted because Round 1 cannot pass.

Earlier-stack hypothesis (untested): `ApplyJustification` calls `DBText.AdjustAlignment` on an `AttributeDefinition` that is not yet
in a database/transaction (needs adding to the block record first). The same path is shared by `FillDefinition`, so
`acad_redefine_block` with attributes is probably affected too.

## 5. Open questions

- Redefined references repainting without manual `REGEN`: **not answered** (round 5 blocked).
- Door's real dynamic property names, kinds, units: **not answered** (round 7 not run).

---

# Retest 2 — 2026-10-27

Driven through the MCP tools directly (no curl). Three builds were involved; every result
below says which one.

| Build | DLL | Server | Where | Notes |
|---|---|---|---|---|
| **A** | `Y:\ACAD_DLL` 17:44, built on the other machine (HEAD + Retest 2 fixes) | pid not recorded, HOTLOAD + MCPSTART 17:47 | Drawing3 | Rounds 1–7 run 17:47–18:28 but never written up. **Reconstructed from `mcp.log` only** — no responses or visual confirmation on record |
| **B** | 18:45, local: Build A with the listener's `ConfigureAwait(false)` removed + main-thread guard | pid 6228, Debug log | Drawing1 (unsaved) | Rounds 1–6 and the round 7 read. **Reverted** — not the committed design |
| **C** | 18:59, local: HEAD threading + the `convert_block_to_static` fix below | pid 15108, Info log, AutoCAD restarted | Drawing3 | Round 1/1b re-check and round 7. **This is the build to keep** |

`tools/list` returned **31** (Build B).

## Per-round results

| Round | Result | Build | Detail |
|---|---|---|---|
| 1 create attributed P4_BOX | **PASS**, user confirmed | A, B, C | `entity_count` 3, `attribute_tags` [TAG, MAKER], `erased_source_count` 1. Definition: TAG at [10,20,0], MAKER constant at [10,5,0], polyline [0,0,0]–[100,50,0]. Build C: `P4_BOX_R3`, inserted `36B` |
| 1b justified attribute (`middle_center`) | **PASS on B, FAIL on C** | B, C | B: `CENTRE` bbox x 3021.3–3078.7, y 19.8–30.2 → centred on (3050, 25), user confirmed. C: `eInvalidContext`, nothing changed — log below |
| 2 inserts + refusals | **PASS**, user confirmed | A, B | R1 `2DD` (0,0), R2 `2E0` (200,0, lowercase `tag` matched), R3 `2E3` (500,0, 90°, scale 2). R3's TAG: height 20, rotation 90°, at (460,20) — transformed with the reference. `MAKER` refused as constant; `NOPE` refused listing `"TAG"` |
| 3 set/get + all-or-nothing | **PASS** | A, B | `changed` 1. Get: A1, R1, R2-B, R3, each with MAKER/WANG under constants, nothing missing. Set on [R1, LINE `2E6`] refused naming `2E6 (LINE)`; R1 unchanged |
| 4 copy attributed block | **PASS**, user confirmed | A, B | Copy `2E7` reads **R1** with its own handle `2E8` |
| 5 redefine (acceptance) | **PASS**, user confirmed box by box | A, B | `removed_entity_count` 3, `entity_count` 4, `references_updated` 5, `values_preserved` 5, `values_reset_to_default` 0, `added_by_tag` {REV:5}. Read back: A1, R1, R2-B, R3, R1 + REV 0 each; all 150 wide (R3 rotated: 100×300) |
| 6 sync, self-reference, refusals | **PASS** | A, B | Sync: 5 synced, 10 preserved, nothing added. P4_OUTER `30A`; redefining P4_BOX from it refused ("would contain itself"). All 8 refusals in the table returned the expected message. `list_blocks`: P4_BOX 5 refs, P4_OUTER 1 |
| 7 dynamic block | **PASS**, user confirmed | A (partly), C | See below |
| 8 units | **DROPPED** | — | Out of scope: only mm drawings will be used. Removed from the brief |

### Round 7 detail (Build C, door `3DE`, Drawing3)

Real property names, kinds and units of *Door - Metric*:

| Property | Kind | Units | Value | Allowed | Read-only |
|---|---|---|---|---|---|
| Door Size | real | mm | 750 | 600, 700, 750, 800, 900, 1000 | no |
| Wall Thickness | real | mm | 100 | 100, 150, 450 | no |
| Hinge | integer | none | 0 | 0, 1 | no |
| Swing | integer | none | 0 | 0, 1 | no |
| Opening Angle | text | none | `Open 30º` | Open 30º/45º/60º/90º, Closed | no |
| Origin (×2) | other | none | (0,0,0) | — | yes |

- Set `Door Size` 900 → `applied` 900; **user confirmed the door got wider**.
- Set 850 → refused, lists allowed values in mm; nothing changed.
- `acad_redefine_block` "Door - Metric" → refused, points to `acad_convert_block_to_static`.
- `acad_convert_block_to_static` → `previous_block_name` "Door - Metric", `new_block_name`
  `P4_DOOR_STATIC`; `list_blocks` shows it `is_dynamic` false. **User confirmed the door is
  static** (no dynamic grips).
- **Bug (reporting only):** `acad_get_dynamic_block_properties` on `3DE` after the convert
  still returns every door property (under block name `P4_DOOR_STATIC`) instead of a
  "not a dynamic block reference" failure. `BlockReference.IsDynamicBlock` stays true on the
  converted reference; the check should also look at the effective definition.

## Failures and fixes

**1. `acad_convert_block_to_static` → `eWasOpenForRead` (Build A, three attempts) — FIXED.**

```
ERROR [  6] op=tool/convert_block_to_static handle=3DA outcome=acad_error error_status=WasOpenForRead
   at BlockReference.ConvertToStaticBlock(String newBlockName)
   at BlockTools.ConvertToStatic(ToolArgs args) BlockTools.cs:line 1804
```

`ConvertToStaticBlock` writes the block table and block records itself, and the transaction
still held the block table (name check) and definitions (`GetEffectiveName`) open ForRead.
Fix: checks in one transaction, commit, then convert in a second transaction that opens only
the reference ForWrite. Passed on Build C.

**2. Justified attribute definitions → `eInvalidContext` (Build C) — OPEN.**

```
ERROR [  7] op=tool/create_block name="P4_CENTRED_R3" outcome=acad_error error_status=InvalidContext
   at DBText.AdjustAlignment(Database alternateDatabaseToUse)
   at TextTools.AdjustIfJustified(DBText dbText, Database db) TextTools.cs:line 287
   at BlockSupport.AddAttributeDefinitions(...) BlockSupport.cs:line 250
```

Left-justified attributes pass only because `AdjustAlignment` is skipped for them. The same
call passed on Build B, where every tool call ran on thread 1; on Builds A and C tool calls
log on varying thread ids (3, 7, 12, 20, 21) while MCPSTART runs on thread 1. Build B's
change was reverted by decision — the threading model stays as committed. Open: either
compute the justified position without `AdjustAlignment`, or keep `create/redefine_block`
to left-justified attributes.

**3. `Editor.Regen()` after redefine → WPF cross-thread exception (Build A) — OPEN, harmless.**

```
WARN  [ 12] op=tool/redefine_block step=regen outcome=failed
System.InvalidOperationException: The calling thread cannot access this object because a different thread owns it.
   at Autodesk.AutoCAD.ViewModel.LayoutSwitch.LayoutTabsData.get_TabsDropDownCollection()
   ...
   at Editor.Regen()
```

The redefine still commits (`regen=False` in the log). On Build B `regenerated` was true.
Either way the user saw the references repaint without typing `REGEN`.

**4. Build B hang — not a Build C issue.** With Build B, after the user ran other commands
(door drag, POLYGON), queued work never ran and every call timed out while AutoCAD stayed
responsive. That is why Build B was reverted. Not seen on Build C.

## Other observations

- **Null extents:** on Build C every INSERT created by a tool in Drawing3 lists `mm_min`/`mm_max`
  `null` in `acad_list_entities` (and the TAG `ATTDEF` in `get_block_definition` on Build B);
  the door placed by hand has proper extents, and so did Build B's inserts in Drawing1.
- `acad_set_block_attributes` refusing constant `MAKER` appends an unrelated hint ("If a tag
  was added to the block definition … run acad_sync_block_attributes first").
- All 33 Build B calls logged `working_db_was_target=False`: the working-database switch ran on
  every call there.
- Unit tests write into the real `mcp.log` (listener tests on ports 63xxx), which clutters it.

## Open questions answered

- **Repaint without manual `REGEN`:** yes — user confirmed the redefined boxes widened on their
  own (Build B, `regenerated` true). On Build A the `Regen` call failed and the log does not
  say whether the user had to regen.
- **Door property names/kinds/units:** table above.
- **HOTLOAD leaving the old server running (run 2):** not reproduced; AutoCAD was restarted
  for Build C.
