# Phase 4 live check — blocks — RESULTS (partial, blocked)

## 1. Verdict

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
