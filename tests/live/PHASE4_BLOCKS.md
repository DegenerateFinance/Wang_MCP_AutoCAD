# Phase 4 live check — blocks

Validating 11 new block tools against a live AutoCAD 2026: `acad_list_blocks`,
`acad_get_block_definition`, `acad_create_block`, `acad_insert_block`,
`acad_get_block_attributes`, `acad_set_block_attributes`, `acad_redefine_block`,
`acad_sync_block_attributes`, `acad_get_dynamic_block_properties`,
`acad_set_dynamic_block_properties`, `acad_convert_block_to_static`.

**None has ever run against a real AutoCAD.** 493 unit tests pass. They prove argument
parsing, the attribute value planning, the sync plan (values kept by tag), the dynamic
property unit conversion and the refusal rules — nothing about what AutoCAD does with them.
The code compiled first time against `acdbmgd.dll` 25.1, so the API names exist; whether they
behave as assumed is what this brief checks.

**Live AutoCAD 2026 session required.** If you cannot reach one, stop and say so — do not
simulate results. One round at a time; **ask the user to confirm what they see** after each.

### Retest 2 (`tests/results/PHASE4_BLOCKS_RESULTS.md` has runs 1 and 2)

Blocks without attributes passed. Every `acad_create_block` with `attributes` has failed with
`eInvalidContext` from `AdjustAlignment` — run 1 before the append, run 2 **after** it, so
"not yet in the database" was not the cause. Current hypothesis: `AdjustAlignment` consults
AutoCAD's **working database**, which follows the drawing `MCPSTART` was started in — and
both runs had three drawings open, writing to Drawing3. This build:

- makes the target drawing the working database for the duration of every tool call, then
  restores it, and logs `working_db_was_target=<bool>` on each call (Debug level);
- skips `AdjustAlignment` entirely for left-justified text, where it is a no-op — which
  covers both attributes in round 1.

**Restart AutoCAD before this run** to be certain the new DLL is the one serving. In run 2,
after `HOTLOAD`, `acad_ping` still showed **42 min uptime**. The same pid is expected
(`HOTLOAD` reloads inside the same process), but the uptime should have reset: `HOTLOAD`
stops the old server (`Terminate`), and `MCPSTART` has to be run again. An unchanged uptime
means the old server never stopped. If you can, reproduce it once in a scratch session —
`HOTLOAD`, `HOTRUN` → `MCPSTART`, `acad_ping` — and record what `HOTLOAD` printed (it should
say `(1 IExtensionApplication(s) initialized)`). That is a separate finding from Phase 4.

1. **Debug logging is required this time** — `HOTRUN` → `MCPLOG` → Debug. The
   `working_db_was_target` value is the evidence that settles the hypothesis.
2. Reproduce run 2's conditions: **start `MCPSTART` while Drawing1 is active**, then switch to
   a fresh drawing and do everything there.
3. Round 1 with the attributed `P4_BOX` exactly as written. It must return `entity_count` 3
   and show `A1` and `WANG` inside the box. Quote the `op=gateway/run … working_db_was_target`
   line for this call — expected `False`, i.e. the switch was needed and happened.
4. **Extra round 1b, the justified case** (the one `AdjustAlignment` is still called for):
   `acad_create_block` name `P4_CENTRED`, no handles, `mm_base_point` (0,−800), one attribute
   `{"tag":"MID","default":"CENTRE","mm_x":50,"mm_y":-775,"mm_height":10,"justify":"middle_center"}`,
   then `acad_insert_block` P4_CENTRED at (3000, 0). **On screen:** `CENTRE` centred on
   (3050, 25). If 1b fails with `InvalidContext` while round 1 passes, the working-database
   hypothesis is wrong and only the left-justified skip is helping — report the log lines.
5. Then continue from round 2. Check `tools/list` returns 31.

Results go in a "Retest 2" section of `tests/results/PHASE4_BLOCKS_RESULTS.md`.

---

## Setup

```
NETLOAD   -> AcadHotLoader_<stamp>.dll          (once per AutoCAD session)
HOTLOAD   -> pick Wang_MCP_AutoCAD.dll          (from C:\Hyper-Shared\ACAD_DLL)
HOTRUN    -> MCPLOG   (Debug)
HOTRUN    -> MCPSTART
```

**One fresh drawing**, active, `INSUNITS` = 4 (mm). Every write goes to the active drawing
(Phase 3b rule). Log: `%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log`, one `op=tool/<name>` line
per call — quote it for any failure.

**Expect 31 tools** from `tools/list`. The four block *read* tools (`acad_list_blocks`,
`acad_get_block_definition`, `acad_get_block_attributes`,
`acad_get_dynamic_block_properties`) describe `drawing` as *"Any open drawing works"*; the
seven write tools as *"Guard … edits ONLY the active drawing"*.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

---

## Round 1 — create an attributed block (the base-point trap)

Draw a 100 × 50 box away from the origin, then turn it into a block with its base point at
the box's lower-left corner, one editable attribute and one constant one:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_rectangle\",\"arguments\":{\"mm_corner1_x\":1000,\"mm_corner1_y\":1000,\"mm_corner2_x\":1100,\"mm_corner2_y\":1050}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_create_block\",\"arguments\":{\"name\":\"P4_BOX\",\"handles\":[\"<RECT>\"],\"mm_base_point_x\":1000,\"mm_base_point_y\":1000,\"attributes\":[{\"tag\":\"TAG\",\"default\":\"A1\",\"mm_x\":1010,\"mm_y\":1020,\"mm_height\":10},{\"tag\":\"MAKER\",\"default\":\"WANG\",\"constant\":true,\"mm_x\":1010,\"mm_y\":1005,\"mm_height\":5}]}}}"
```

**Response:** `name` = `"P4_BOX"`; `entity_count` = `3` (box + 2 attribute definitions);
`attribute_tags` = `["TAG","MAKER"]`; `source_action` = `"replace"`; `erased_source_count` = `1`;
an `inserted_handle` (call it `<R0>`).

**On screen:** the box has **not moved** — still from (1000,1000) to (1100,1050), now a block
(click it: it selects as one object). Inside it: `A1` (10 high) and `WANG` (5 high) near its
lower-left corner.

- Box shown at **(2000,2000)** → the clones were not shifted by minus the base point (or
  were shifted twice). This is the #1 block-creation bug; report and stop.
- Box at the right place but **no text** → attributes were not built on the replacement
  reference.

Then read it back:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_block_definition\",\"arguments\":{\"name\":\"P4_BOX\"}}}"
```

**Response:** `attribute_definitions` has `TAG` with `mm_position` ≈ `[10,20,0]` and
`MAKER` with `is_constant: true` at `[10,5,0]` — **block-space** coordinates, relative to the
base point. The polyline's `mm_min`/`mm_max` ≈ `[0,0,0]`/`[100,50,0]`. `reference_count` = 1.

→ **Ask the user to confirm** before round 2.

---

## Round 2 — insert with attribute values, rotation and scale

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_insert_block\",\"arguments\":{\"name\":\"P4_BOX\",\"mm_position_x\":0,\"mm_position_y\":0,\"attributes\":{\"TAG\":\"R1\"}}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_insert_block\",\"arguments\":{\"name\":\"P4_BOX\",\"mm_position_x\":200,\"mm_position_y\":0,\"attributes\":{\"tag\":\"R2\"}}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_insert_block\",\"arguments\":{\"name\":\"P4_BOX\",\"mm_position_x\":500,\"mm_position_y\":0,\"deg_rotation\":90,\"scale\":2,\"attributes\":{\"TAG\":\"R3\"}}}}"
```

**Response (each):** a `handle` (`<R1>`, `<R2>`, `<R3>`); `attributes` lists `TAG` with the
value sent and its own handle; `constant_attributes` lists `MAKER` = `WANG`. The second call
used lowercase `tag` — it must still match.

**On screen:**
- `R1` box with its lower-left corner **at the origin**, reading `R1` and `WANG`.
- `R2` box at (200,0), reading `R2`.
- `R3` box **twice the size, rotated 90° counter-clockwise** about (500,0) — so it extends
  *left* of x=500 and *up* to y=200 — with `R3` and `WANG` rotated and scaled **with** it. Text
  that stayed horizontal or small means `SetAttributeFromBlock` did not get the
  reference's transform.

Then two refusals (nothing must be inserted):

| Arguments on `acad_insert_block` (name P4_BOX, position 0,300) | Expect |
|---|---|
| `"attributes":{"MAKER":"X"}` | constant, cannot be set per reference |
| `"attributes":{"NOPE":"X"}` | unknown tag, lists settable `"TAG"` |

→ **Ask the user to confirm** before round 3.

---

## Round 3 — read and change attribute values

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_block_attributes\",\"arguments\":{\"handles\":[\"<R2>\"],\"attributes\":{\"TAG\":\"R2-B\"}}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_block_attributes\",\"arguments\":{\"handles\":[\"<R0>\",\"<R1>\",\"<R2>\",\"<R3>\"]}}}"
```

**Response:** set → `changed` 1, `attributes_changed` 1. Get → four references; `TAG` values
`A1`, `R1`, `R2-B`, `R3`; each lists `MAKER`/`WANG` under `constant_attributes`;
`tags_missing_from_reference` empty.

**On screen:** the R2 box now reads `R2-B`. Nothing else changed.

All-or-nothing: `acad_set_block_attributes` with `handles` `["<R1>","<RECT_OR_ANY_NON_BLOCK>"]`
(draw a quick line for the second) and `{"TAG":"MUST NOT APPLY"}` → `isError`, names the
non-block handle; R1 still reads `R1`.

→ **Ask the user to confirm** before round 4.

---

## Round 4 — Phase 3 carry-over: copying an attributed block

Phase 3 switched copies to `DeepCloneObjects` precisely for this; it was never checked live.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_copy\",\"arguments\":{\"handles\":[\"<R1>\"],\"mm_displacement_x\":0,\"mm_displacement_y\":-200}}}"
```

Call the new handle `<R1C>`, then read it with `acad_get_block_attributes`.

**Response:** `TAG` = **`R1`**, with its own new handle.
**On screen:** a box at (0,−200) reading **`R1`** and `WANG`.

- **No `R1` text at all** → the copy was shallow; attributes lost. Report and stop.
- **`A1`** → attributes were rebuilt from the definition instead of cloned.

→ **Ask the user to confirm** before round 5.

---

## Round 5 — THE acceptance test: redefine, adding an attribute, values kept

Five references exist (`R0`, `R1`, `R2`, `R3`, `R1C`) with values `A1`, `R1`, `R2-B`, `R3`, `R1`.
Redefine P4_BOX as a **wider** box with a **second** attribute `REV`:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_rectangle\",\"arguments\":{\"mm_corner1_x\":1000,\"mm_corner1_y\":1000,\"mm_corner2_x\":1150,\"mm_corner2_y\":1050}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_redefine_block\",\"arguments\":{\"name\":\"P4_BOX\",\"handles\":[\"<RECT2>\"],\"mm_base_point_x\":1000,\"mm_base_point_y\":1000,\"source_action\":\"erase\",\"attributes\":[{\"tag\":\"TAG\",\"default\":\"A1\",\"mm_x\":1010,\"mm_y\":1020,\"mm_height\":10},{\"tag\":\"REV\",\"default\":\"0\",\"mm_x\":1110,\"mm_y\":1020,\"mm_height\":10},{\"tag\":\"MAKER\",\"default\":\"WANG\",\"constant\":true,\"mm_x\":1010,\"mm_y\":1005,\"mm_height\":5}]}}}"
```

**Response:** `removed_entity_count` = 3; `entity_count` = 4; `references_updated` = **5**;
`sync.values_preserved` = **5**; `sync.values_reset_to_default` = 0;
`sync.added_by_tag` = `{"REV":5}`; `sync.removed_by_tag` = `{}`; `regenerated` = true.

**On screen — all five boxes:**
- are **150 wide** now (geometry updated in every reference, including the rotated one);
- show **their original TAG value** — `A1`, `R1`, `R2-B`, `R3`, `R1` — **not** `A1` everywhere;
- show a new `0` (REV) on the right-hand side;
- still show `WANG`.

`TAG` reset to `A1` on every box is **silent data loss**, the single most important bug this
phase guards against — report it and stop. Also note whether the boxes repainted on their own
(the tool calls `Regen`) or only after typing `REGEN`; that is an open question from metadata.

Confirm by reading `<R2>` with `acad_get_block_attributes`: `TAG` = `R2-B`, `REV` = `0`.

→ **Ask the user to confirm** before round 6.

---

## Round 6 — sync on its own, and the refusals

1. `acad_sync_block_attributes` `{"name":"P4_BOX"}` → `references_synced` 5,
   `values_preserved` 10 (TAG + REV on each), `added_by_tag` `{}`. **On screen:** nothing changes.

2. Self-reference: make a block that contains a P4_BOX reference, then try to redefine P4_BOX
   from it.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_create_block\",\"arguments\":{\"name\":\"P4_OUTER\",\"handles\":[\"<R3>\"],\"mm_base_point_x\":500,\"mm_base_point_y\":0}}}"
```

Take its `inserted_handle` as `<OUTER>`, then:
`acad_redefine_block` `{"name":"P4_BOX","handles":["<OUTER>"],"mm_base_point_x":0,"mm_base_point_y":0}`
→ `isError`, *"…contains "P4_BOX" … would contain itself"*. P4_BOX unchanged on screen.

3. Refusals table, each `isError` and nothing changed:

| Call | Expect |
|---|---|
| `acad_create_block` name `P4_BOX` (exists) | "already exists", points to `acad_redefine_block` |
| `acad_create_block` name `*X` | starts with '*', reserved |
| `acad_create_block` with no `handles` and no `attributes` | at least one is required |
| `acad_create_block` attribute tag `"PART NO"` | contains a space |
| `acad_insert_block` name `NOPE` | does not exist, points to `acad_list_blocks` |
| `acad_insert_block` `scale` 2 **and** `scale_y` 3 | not both |
| `acad_redefine_block` name `*Model_Space` | layout |
| `acad_set_block_attributes` on `<R1>` with `{"MAKER":"X"}` | constant |

4. `acad_list_blocks` → P4_BOX (`has_attributes` true, `reference_count` 5) and P4_OUTER
   (`reference_count` 1). No `*Model_Space`, no `*U…` entries. With
   `"include_anonymous":true` the anonymous ones (if any) appear.

→ **Ask the user to confirm** before round 7.

---

## Round 7 — dynamic blocks (needs one from AutoCAD's tool palettes)

The server cannot author dynamic blocks (no managed API). Get one by hand: **Ctrl+3** (Tool
Palettes) → *Architectural* tab → drag a **Door - Metric** into the drawing. Find its handle
with `acad_list_entities` (`type` INSERT, newest last) — call it `<DOOR>`.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_dynamic_block_properties\",\"arguments\":{\"handles\":[\"<DOOR>\"]}}}"
```

**Response:** a `properties` list with names such as `Door Size`, `Wall Thickness`, `Open %`
(exact names depend on the palette version), each with `kind`, `units` and `value`. Distances
must be in **mm** with plausible door sizes (e.g. 750–1000), **not** 0.75 or 750000. Angles
in degrees. `Origin` and similar show `read_only: true`.

Pick a settable distance property with an `allowed_values` list (likely `Door Size`) and a
value from that list, then:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":15,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_dynamic_block_properties\",\"arguments\":{\"handles\":[\"<DOOR>\"],\"properties\":{\"<PROPERTY_NAME>\":<ALLOWED_VALUE_MM>}}}}"
```

**Response:** `applied` shows the property at the value sent. **On screen:** the door changes
to that width. Then a value **not** in the list → `isError` listing the allowed values in mm,
door unchanged.

Then convert and redefine:

1. `acad_redefine_block` with the door's `block_name` → `isError`, dynamic, points to
   `acad_convert_block_to_static`.
2. `acad_convert_block_to_static` `{"handle":"<DOOR>","new_block_name":"P4_DOOR_STATIC"}` →
   `previous_block_name` is the door block, `new_block_name` `P4_DOOR_STATIC`. **On screen:**
   the door looks identical; selecting it shows no dynamic grips any more.
3. `acad_get_dynamic_block_properties` on `<DOOR>` → now reported under `failures` ("not a
   dynamic block reference").

→ **Ask the user to confirm** before round 8.

---

## Round 8 — units (also covers the skipped Phase 3 round 6)

Only mm drawings have been exercised so far, in any phase.

1. Set `INSUNITS` = **6 (metres)**.
2. `acad_insert_block` P4_BOX at `mm_position` (2000, 3000), `attributes` `{"TAG":"UNITS"}`.
   **Response:** `mm_per_drawing_unit` = 1000. **`LIST`** the new reference: insertion point
   **(2, 3)** drawing units. At (2000, 3000) → the position was not converted.
3. `acad_get_entity_properties` on it → `mm_position` = `[2000,3000,0]` (the round trip).
4. While still in metres, run **Phase 3 round 6** from `PHASE3_TRANSFORMS.md` (move/copy a line
   by mm and confirm drawing-unit results), and its round 4b (mirror without `copy`).
5. Set `INSUNITS` back to **4**.

---

## What to come back with

Write `tests/results/PHASE4_BLOCKS_RESULTS.md`:

1. **Verdict** — one line per tool group: create/insert, attributes, redefine/sync, dynamic.
2. **Environment** — AutoCAD build, DLL path/timestamp, AcadHotLoader vs direct NETLOAD, log level.
3. **Per-round results** — pass / fail / not run, with handles, and that the user visually
   confirmed each one.
4. **Failures** — what was sent, what came back, log lines verbatim, what AutoCAD did.
5. **Open questions answered** — did redefined references repaint without a manual `REGEN`?
   What are the door's real property names, kinds and units?

Report, do not refactor. Rounds 1, 4 and 5 are the ones most easily mistaken for a pass: in
round 5 especially, check the TAG values box by box rather than at a glance.
