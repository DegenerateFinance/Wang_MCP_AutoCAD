# Phase 2 live check — text tools

Validating three new MCP tools against a live AutoCAD 2026: `acad_add_text`, `acad_add_mtext`,
`acad_set_text_content`. **None has ever run against a real AutoCAD.** Everything testable
without AutoCAD passes (340 unit tests), which proves the wire-name → enum mapping and
nothing about AutoCAD's response to it.

**Changed since this brief was written:** `acad_add_text` now calls `AdjustAlignment` *after*
appending the text rather than before. The pre-append call threw `eInvalidContext` for
attribute definitions in Phase 4; it was moved for plain text too. Round 2 (the
`AlignmentPoint` trap) is therefore also the check that this reordering still centres text
correctly. Since Phase 3b every write goes to the **active** drawing only; work in one active
drawing. Results go to `tests/results/PHASE2_TEXT_RESULTS.md`.

**You need a live AutoCAD 2026 session with a drawing open.** If you cannot reach one, stop
and say so — do not simulate results.

Work one round at a time: run the calls, check the JSON, then **stop and ask the user to
confirm what they see on screen** before moving on. A response can look perfect while the
drawing is wrong; that gap is the whole point of this document.

---

## Setup

```
NETLOAD   -> AcadHotLoader_<stamp>.dll          (once per AutoCAD session)
HOTLOAD   -> pick Wang_MCP_AutoCAD.dll          (from C:\Hyper-Shared\ACAD_DLL)
HOTRUN    -> MCPSTART
```

`HOTLOAD` must report `(1 IExtensionApplication(s) initialized)`. `0` means the wrong DLL.

Then `HOTRUN` → `MCPLOG` and set the level to **Debug**. Log:
`%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log`. Quote from it for any failure.

Set the drawing to **`INSUNITS` = 4 (millimetres)** for rounds 1–4, so mm and drawing units
are 1:1 and a wrong number is obvious by eye. Round 5 changes it deliberately.

Use `curl.exe`, not `curl` — in PowerShell the bare name is an alias for `Invoke-WebRequest`.
Endpoint is `http://127.0.0.1:5599/mcp`.

**Expect 31 tools** from `tools/list` (17 when this brief was written; Phases 3, 3b and 4
added the rest), including the three under test:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

---

## Round 1 — plain text, the baseline case

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_text\",\"arguments\":{\"text\":\"PHASE2 LEFT\",\"mm_position_x\":0,\"mm_position_y\":0,\"mm_height\":10}}}"
```

**Response:** a `handle`; `justify` = `"left"`; `mm_height` = `10`;
`height_overridden_by_style` = `false`; `mm_per_drawing_unit` = `1`.

**On screen** (`ZOOM` → `Extents`): `PHASE2 LEFT` reading left-to-right, 10 mm cap height,
its **bottom-left corner at the origin**. Text 10× too large or too small means the unit
conversion is wrong.

→ Report the handle, then **ask the user to confirm** before round 2.

---

## Round 2 — the AlignmentPoint trap (the one that matters most)

This is the check metadata cannot settle. For any justification other than `left`, AutoCAD
reads `AlignmentPoint` and **ignores** `Position`. The code sets both and calls
`AdjustAlignment`. If that is wrong, text lands at the origin instead of where you asked.

Draw a marker circle first so "centred on the point" is judged against something real:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_circle\",\"arguments\":{\"mm_center_x\":200,\"mm_center_y\":100,\"mm_radius\":3}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_text\",\"arguments\":{\"text\":\"CENTRED\",\"mm_position_x\":200,\"mm_position_y\":100,\"mm_height\":10,\"justify\":\"middle_center\"}}}"
```

**Response:** `justify` = `"middle_center"`.

**On screen:** `CENTRED` is **centred on the small circle at (200, 100)** — the circle sits
in the middle of the word, both horizontally and vertically.

- Text *starting* at the circle instead of centred on it → `AlignmentPoint` was not applied.
- Text **at the origin (0, 0)** instead → the trap fired. That is the highest-value bug in
  this phase; report it and stop.

Then repeat with `"justify":"top_right"` at `mm_position_x: 400, mm_position_y: 100` (add a
marker circle there too). Expect the text's **top-right corner** at the marker, i.e. the word
sitting down and to the left of it.

→ **Ask the user to confirm both** before round 3.

---

## Round 3 — mtext, wrapping and escaping

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_mtext\",\"arguments\":{\"text\":\"This paragraph should wrap onto several lines because the column is narrow.\",\"mm_location_x\":0,\"mm_location_y\":-100,\"mm_height\":8,\"mm_width\":150}}}"
```

**Response:** `wrapped` = `true`; `mm_actual_width` ≈ 150 or less (**not 0** — a 0 here means
the measured size was read before the entity reached the database); `mm_actual_height`
clearly greater than 8, since it wrapped to several lines; `attachment` = `"top_left"`.

**On screen:** a paragraph wrapping inside roughly a 150 mm column, growing **downward** from
(0, −100) — its top-left corner at that point.

Then the escaping case, which is why `MTextEscaper` exists:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_mtext\",\"arguments\":{\"text\":\"Path C:\\\\Temp\\\\Job{01} 50%%\",\"mm_location_x\":0,\"mm_location_y\":-200,\"mm_height\":8,\"mm_width\":0}}}"
```

**Response:** `escaped` = `true`; `contents` shows the escaped form (doubled backslashes,
`\{`), while `text` echoes what you sent; `wrapped` = `false`.

**On screen:** the text displays **literally** — `Path C:\Temp\Job{01} 50%%`, one line, no
wrapping. Missing backslashes, vanished braces, or the line breaking into pieces means the
escaping is wrong and MText read the string as formatting codes.

→ Report both handles and **ask the user to confirm** before round 4.

---

## Round 4 — editing existing text

Use the round 1 handle (`PHASE2 LEFT`) and the round 3 paragraph handle. Substitute the real
handles below.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_text_content\",\"arguments\":{\"handles\":[\"<HANDLE_R1>\",\"<HANDLE_R3>\"],\"text\":\"REPLACED\"}}}"
```

**Response:** `changed` = `2`; `text_entities` = `1`; `mtext_entities` = `1`;
`attributes` = `0`.

**On screen:** both the single-line text and the paragraph now read `REPLACED`. The edited
single-line text must **keep its original justification and position** — round 1's was
baseline-left at the origin, so it should not have jumped.

Then the all-or-nothing guard. Mix a good handle with a nonsense one:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_text_content\",\"arguments\":{\"handles\":[\"<HANDLE_R1>\",\"ZZZZ\"],\"text\":\"MUST NOT APPLY\"}}}"
```

**Response:** `isError: true`, naming `ZZZZ`.
**On screen:** the valid entity still reads `REPLACED` — **not** `MUST NOT APPLY`. If it
changed, the validate-before-mutate guard is broken and a partial edit got through.

And pointing it at non-text — pass the marker circle's handle from round 2:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_set_text_content\",\"arguments\":{\"handles\":[\"<CIRCLE_HANDLE>\"],\"text\":\"NOPE\"}}}"
```

**Response:** `isError: true`, naming the handle and its type (`CIRCLE`), and mentioning that
the tool works on TEXT, MTEXT and ATTRIB.

→ **Ask the user to confirm** before round 5.

---

## Round 5 — units

The mm/du split is the most likely thing to be silently wrong.

1. Still at `INSUNITS` = 4, add text with `mm_height: 25`. Measure it in AutoCAD: cap height
   **25 drawing units**, `mm_per_drawing_unit` = `1`.
2. Set `INSUNITS` to **6 (metres)**. Add text with `mm_height: 25` again at a clear spot.
   Response: `mm_per_drawing_unit` = `1000`.
3. **Measure both.** They must be the **same physical size** on screen. If the second is
   1000× bigger or smaller, the conversion is inverted — the most valuable bug here.
4. Set `INSUNITS` to **0 (unitless)**. Any text call must return a clean `isError: true`
   naming `INSUNITS`, **not** silently assume millimetres.

→ **Ask the user to confirm the two heights match** before round 6.

---

## Round 6 — bad input, should be `isError` and never a crash

Each must be HTTP 200 with `isError: true` and a message naming the offending field:

- `mm_height: 0` and `mm_height: -5`
- `text: ""` (empty) and `text: "   "` (whitespace only)
- `"text":"line one\nline two"` on **`acad_add_text`** — must be refused and point at
  `acad_add_mtext`; a single-line TEXT cannot hold a line break
- `justify: "centre"` (British spelling) and `justify: "base_fit"` — neither is advertised;
  the error must list the 12 valid names
- `text_style: "NO_SUCH_STYLE"` → must name the style and suggest the STYLE command
- `layer: "DOES_NOT_EXIST"` → must suggest `acad_create_layer`
- `mm_width: -5` on `acad_add_mtext` → must say 0 or greater, and that 0 means no wrapping

AutoCAD must be unharmed throughout — no dialogs, no frozen UI, no crash.

---

## Round 7 — text style with a fixed height

Needs a style whose height is not 0. In AutoCAD: `STYLE`, new style `FIXED10`, set height
`10`, apply, close. Then:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":20,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_add_text\",\"arguments\":{\"text\":\"FIXED HEIGHT\",\"mm_position_x\":0,\"mm_position_y\":-400,\"mm_height\":50,\"text_style\":\"FIXED10\"}}}"
```

**Response:** `height_overridden_by_style` = `true`, and `mm_height` reporting the height the
text **actually** has, not the 50 that was asked for.

**On screen:** the text is 10 units tall, not 50 — the style wins, and the response said so.

---

## Round 8 — round-trip read

For one entity from each of rounds 1, 2 and 3:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":21,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"handles\":[\"<H1>\",\"<H2>\",\"<H3>\"]}}}"
```

Every value must match what was written: `text`, `mm_height`, `deg_rotation`, `justify` /
`attachment`, and position. This catches unit and angle bugs mechanically rather than by eye —
particularly `deg_rotation`, where a missing radian conversion shows up as a number in the
thousands instead of 0.

---

## What to come back with

1. **Verdict** — one line: do the three text tools work end to end?
2. **Environment** — AutoCAD build, the DLL path and timestamp actually loaded, dev loop vs
   direct `NETLOAD`, and the drawing's `INSUNITS`.
3. **Per-round results** — pass / fail / not run, with returned handles, and state explicitly
   that the user visually confirmed each one.
4. **Failures** — what was sent, what came back, the log lines verbatim, and what AutoCAD did.
5. **Anything surprising** — dialogs, freezes, anything this brief did not predict.

Report, do not refactor. If you change something trivial on the way, say exactly what.

Negative results are the valuable ones. Rounds 2, 5 step 3 and 7 are the three easiest to
mistake for a pass — if everything passes first time, double-check those.
