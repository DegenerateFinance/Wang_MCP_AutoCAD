# Phase 3 live check — transforms and copy

Validating two new MCP tools against a live AutoCAD 2026: `acad_transform` (move / rotate /
scale / mirror, optionally as a copy) and `acad_copy` (displacement × count). **Neither has
ever run against a real AutoCAD.** 369 unit tests pass; they prove argument parsing, the
mode/argument guard and the copy offsets — nothing about what AutoCAD does with the matrix.

**You need a live AutoCAD 2026 session with a drawing open.** If you cannot reach one, stop
and say so — do not simulate results.

Work one round at a time: run the calls, check the JSON, then **stop and ask the user to
confirm what they see on screen** before moving on.

Ideally run `PHASE2_TEXT.md` first — round 4 here mirrors text, and is only meaningful if
text placement is already known to be right.

---

## Setup

```
NETLOAD   -> AcadHotLoader_<stamp>.dll          (once per AutoCAD session)
HOTLOAD   -> pick Wang_MCP_AutoCAD.dll          (from C:\Hyper-Shared\ACAD_DLL)
HOTRUN    -> MCPSTART
```

`HOTLOAD` must report `(1 IExtensionApplication(s) initialized)`. `MCPLOG` → **Debug**.
Log: `%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log` — every call writes one `op=tool/transform` or
`op=tool/copy` line with handles and `elapsed_ms`; quote it for any failure.

Start a **fresh drawing**, `INSUNITS` = **4 (millimetres)**. Use `curl.exe`, not `curl`.
Endpoint `http://127.0.0.1:5599/mcp`.

**Expect 31 tools** from `tools/list` (19 when this brief was written), including `acad_transform` and `acad_copy`:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

---

## Round 1 — rotate: the angle convention (most likely to be wrong)

Draw a 100 mm horizontal line, then rotate it 90° about its left end:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_line\",\"arguments\":{\"mm_start_x\":0,\"mm_start_y\":0,\"mm_end_x\":100,\"mm_end_y\":0}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_transform\",\"arguments\":{\"handles\":[\"<LINE1>\"],\"mode\":\"rotate\",\"mm_base_x\":0,\"mm_base_y\":0,\"deg_angle\":90}}}"
```

**Response:** `mode` = `"rotate"`; `transformed` = `1`; `copy` = `false`; `new_handles` = `[]`;
`parameters.deg_angle` = `90`; `mirrored_text_count` = `0`.

**On screen:** the line points **straight up**, from (0, 0) to (0, 100).

- Points **down** → the sign of the angle is inverted (should be counter-clockwise).
- Sits at about **117°** (up and to the left) → degrees were passed as radians (90 rad ≡ 116.6°).
- Looks like a **dot** in plan view, or `LIST` shows a Z of ±100 → rotated about the wrong axis
  (out of plane). That is the `Vector3d.ZAxis` trap.

Confirm with a read-back:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"handles\":[\"<LINE1>\"]}}}"
```

End point ≈ `[0, 100, 0]` (tiny floating error like `6e-15` is fine).

→ **Ask the user to confirm** before round 2.

---

## Round 2 — move and scale

Move the same line up by 200 using two points, then scale a circle about its centre:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_transform\",\"arguments\":{\"handles\":[\"<LINE1>\"],\"mode\":\"move\",\"mm_from_x\":0,\"mm_from_y\":0,\"mm_to_x\":0,\"mm_to_y\":200}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_circle\",\"arguments\":{\"mm_center_x\":300,\"mm_center_y\":0,\"mm_radius\":10}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_transform\",\"arguments\":{\"handles\":[\"<CIRCLE1>\"],\"mode\":\"scale\",\"mm_base_x\":300,\"mm_base_y\":0,\"factor\":3}}}"
```

**Response:** move → `parameters.mm_from` = `[0,0,0]`, `mm_to` = `[0,200,0]`.
Scale → `parameters.factor` = `3`, `mirrored_text_count` = `0`.

**On screen:** the line now runs from (0, 200) to (0, 300). The circle is **radius 30, still
centred at (300, 0)** — it grew in place. A circle that also slid away from (300, 0) means
the base point was ignored (scaled about the origin instead).

→ **Ask the user to confirm** before round 3.

---

## Round 3 — copy along a row

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_rectangle\",\"arguments\":{\"mm_corner1_x\":0,\"mm_corner1_y\":-200,\"mm_corner2_x\":50,\"mm_corner2_y\":-150}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_copy\",\"arguments\":{\"handles\":[\"<RECT1>\"],\"mm_displacement_x\":100,\"mm_displacement_y\":0,\"count\":3}}}"
```

**Response:** `count` = `3`; `new_entity_count` = `3`; `copies` has three entries with
`mm_offset` `[100,0,0]`, `[200,0,0]`, `[300,0,0]`, each with one new handle, all different
from `<RECT1>` and from each other.

**On screen:** **four** identical 50 × 50 squares in a row along y = −200…−150, left edges at
x = 0, 100, 200, 300. Original untouched. Squares at 100, 200, 200 (stacked) would mean the
offsets are not cumulative; one square at 300 with nothing between means only the last copy
survived.

→ **Ask the user to confirm** before round 4.

---

## Round 4 — mirror as a copy, and backwards text

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":10,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_text\",\"arguments\":{\"text\":\"MIRROR\",\"mm_position_x\":500,\"mm_position_y\":200,\"mm_height\":10}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":11,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_line\",\"arguments\":{\"mm_start_x\":500,\"mm_start_y\":180,\"mm_end_x\":580,\"mm_end_y\":180}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":12,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_transform\",\"arguments\":{\"handles\":[\"<TEXT_M>\",\"<LINE_M>\"],\"mode\":\"mirror\",\"copy\":true,\"mm_line_start_x\":600,\"mm_line_start_y\":0,\"mm_line_end_x\":600,\"mm_line_end_y\":1}}}"
```

**Response:** `copy` = `true`; `transformed` = `2`; `new_handles` has **2** handles, in the
same order as `handles`; `mirrored_text_count` = `1`.

**On screen:** originals unchanged on the left of x = 600. On the right, a mirror image: line
from (620, 180) to (700, 180), and the text **reading backwards** (`ЯOЯЯIM`-style) and ending
at x = 700. **Backwards text here is the expected, documented behaviour** — the tool reports
it via `mirrored_text_count` and deliberately does not "fix" it. Text that reads normally
would actually be surprising and worth reporting.

Then mirror in place (no copy) to confirm `copy` defaults to false: run the same call without
`"copy":true` on `<LINE_M>` alone → `new_handles` = `[]`, and the original line jumps across to
(620…700, 180).

→ **Ask the user to confirm** before round 5.

---

## Round 5 — copying an attributed block (the reason copies use DeepCloneObjects)

There is no block tool yet (Phase 4), so build one by hand. In AutoCAD:

1. `ATTDEF` → tag `TAG`, default `A1`, height 5, place it near (0, −400).
2. Draw a small circle around it. `BLOCK` → name `ATTBLK`, base point (0, −400), select both,
   *Convert to block* off / *Delete*.
3. `INSERT` `ATTBLK` at (0, −400), enter the value **`KEEP`** when prompted.
4. `LIST` the reference and note its `Handle`.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":13,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_copy\",\"arguments\":{\"handles\":[\"<BLKREF>\"],\"mm_displacement_x\":0,\"mm_displacement_y\":-100}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":14,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"handles\":[\"<NEW_BLKREF>\"]}}}"
```

**Response:** one new handle; the read-back shows an attribute `TAG` = `KEEP`.

**On screen:** the copy at (0, −500) shows **`KEEP`**. A copy with **no attribute text at
all** means the clone was shallow — that is exactly the bug the switch away from
`GetTransformedCopy` was meant to prevent; report it. A copy showing `A1` means attributes
were rebuilt from the definition instead of cloned.

→ **Ask the user to confirm** before round 6.

---

## Round 6 — units

1. Set `INSUNITS` to **6 (metres)**. Draw a line from mm (0, 0) to (1000, 0) — that is 1 drawing
   unit long.
2. Move it with `mm_from` (0, 0) → `mm_to` (1000, 1000).
   **Response:** `mm_per_drawing_unit` = `1000`.
   **On screen / `LIST`:** the line runs from (1, 1) to (2, 1) **drawing units**. If it went
   to (1000, 1000) the displacement was not converted.
3. `acad_copy` it with `mm_displacement_x: 500` → copy at x = 1.5…2.5 drawing units.
4. Set `INSUNITS` back to **4**.

→ **Ask the user to confirm** before round 7.

---

## Round 7 — guards; each must be `isError: true` and leave the drawing untouched

Use `<RECT1>` from round 3 unless stated.

| Call | Expect in the error | Must NOT happen |
|---|---|---|
| `acad_transform` move with `"handles":["<RECT1>","<RECT1>"]` | "more than once" | square moves (twice as far) |
| `acad_transform` mode `scale`, `factor` 2, **plus** `deg_angle` 30 | names `"deg_angle"`, says scale takes `factor` | anything scaled |
| `acad_transform` mode `rotate` with no `deg_angle` | `deg_angle` missing | — |
| `acad_transform` mode `mirror`, both line points (5, 5) | "identical" | — |
| `acad_transform` move with `["<RECT1>","ZZZZ"]` | names `ZZZZ`, "nothing was changed" | square moves |
| `acad_copy` with displacement (0, 0) | "zero", "on top" | a hidden duplicate |
| `acad_copy` with `count` 101 | count between 1 and 100 | — |

Then the locked layer:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_create_layer\",\"arguments\":{\"name\":\"P3_LOCKED\"}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_line\",\"arguments\":{\"mm_start_x\":0,\"mm_start_y\":-700,\"mm_end_x\":100,\"mm_end_y\":-700,\"layer\":\"P3_LOCKED\"}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":22,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_layer_state\",\"arguments\":{\"name\":\"P3_LOCKED\",\"is_locked\":true}}}"
```

- `acad_transform` move on that line → `isError`, mentions a locked layer and
  `acad_set_layer_state`. Line does not move.
- `acad_copy` on that line → **predicted** `isError` with the same message (the clone lands on
  the locked layer and cannot be opened to displace it). **Not knowable from metadata** —
  if it instead succeeds and a copy appears at the right offset, report that as a finding,
  not a failure.

AutoCAD must be unharmed throughout — no dialogs, no frozen UI.

---

## What to come back with

1. **Verdict** — one line: do both tools work end to end?
2. **Environment** — AutoCAD build, DLL path and timestamp loaded, dev loop vs direct
   `NETLOAD`, `INSUNITS`.
3. **Per-round results** — pass / fail / not run, returned handles, and that the user visually
   confirmed each one.
4. **Failures** — what was sent, what came back, the log lines verbatim, what AutoCAD did.
5. **Anything surprising.**

Report, do not refactor.

Rounds 1, 5 and 6 are the ones that can only fail live. If everything passes first time,
re-check round 1's read-back numbers and round 5's attribute value before celebrating.
