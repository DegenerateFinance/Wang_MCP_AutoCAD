# Phase 3b live check — several open drawings (retest)

The first run (`tests/results/PHASE3B_DRAWINGS_RESULTS.md`) showed that the running server can **read** a
drawing that is not active, but **cannot write** to one (`WrongDatabase`). The server now
enforces that as a rule:

- **Write tools edit the active drawing only.** On them, `drawing` is a **guard**: if it names
  any other open drawing, the call is refused **before** reaching AutoCAD, with a message
  naming the active drawing — no more raw `WrongDatabase`.
- **Read tools** — `acad_list_entities`, `acad_get_entity_properties`, `acad_list_layers` —
  still accept `drawing` and read any open drawing.
- Every response still names its drawing (`drawing`, `drawing_path`, `drawing_is_active`).

**The refusal path has never run against a real AutoCAD.** 411 unit tests pass; they prove
the rule's logic and that each tool is flagged correctly, not AutoCAD's behaviour.

**Live AutoCAD 2026 session required.** If you cannot reach one, stop and say so — do not
simulate results. One round at a time; **ask the user to confirm** after each.

---

## Setup

```
NETLOAD   -> AcadHotLoader_<stamp>.dll          (once per AutoCAD session)
HOTLOAD   -> pick Wang_MCP_AutoCAD.dll          (from C:\Hyper-Shared\ACAD_DLL)
HOTRUN    -> MCPSTART
```

**Use AcadHotLoader, not a direct `NETLOAD` of `Wang_MCP_AutoCAD.dll`** — a direct NETLOAD locks
the file and the next build fails to deploy.

`MCPLOG` → **Debug** this time, so the gateway's lines are captured:
`op=gateway/run drawing="<path>" is_active=<bool> outcome=done`, and for refusals
`op=gateway/run requested_drawing="<name>" require_active=True outcome=refused_not_active`.

Two drawings open, both `INSUNITS` = 4. Below they are **`Drawing1.dwg`** and
**`Drawing2.dwg`**, with **Drawing2 active**, as in the first run. Use scratch drawings: rounds
1 and 4 add geometry.

**Expect 31 tools** from `tools/list` (20 when this brief was written). Look at the `drawing` property's description:
**write** tools (e.g. `acad_draw_line`) say *"Guard … edits ONLY the active drawing"*;
**read** tools (`acad_list_entities`, `acad_get_entity_properties`, `acad_list_layers`) say
*"Any open drawing works"*.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}"
```

---

## Round 1 — write to the active drawing, guard matching

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_line\",\"arguments\":{\"drawing\":\"Drawing2\",\"mm_start_x\":0,\"mm_start_y\":0,\"mm_end_x\":200,\"mm_end_y\":100}}}"
```

**Response:** a `handle`; `drawing` = `"Drawing2.dwg"`; `drawing_is_active` = `true`.
**On screen:** a diagonal line in **Drawing2**, from the origin up to (200, 100).

→ **Ask the user to confirm** before round 2.

---

## Round 2 — write naming the non-active drawing: clean refusal

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_line\",\"arguments\":{\"drawing\":\"Drawing1\",\"mm_start_x\":0,\"mm_start_y\":0,\"mm_end_x\":200,\"mm_end_y\":100}}}"
```

**Response:** `isError: true`, text exactly of the form:
`"Drawing1.dwg" is not the active drawing, so nothing was changed. This server edits the
active drawing only: switch to it in AutoCAD, then retry. Active: Drawing2.dwg. …`
It must **not** mention `WrongDatabase`. Log: `outcome=refused_not_active`.

**On screen:** nothing new in either drawing; AutoCAD stays on Drawing2.

Then one more write tool, to confirm the rule covers more than drawing (use the round 1
handle — the guard is checked before the handle is even looked up):

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_delete_entities\",\"arguments\":{\"drawing\":\"Drawing1\",\"handles\":[\"<R1_LINE>\"]}}}"
```

**Response:** the same refusal. **On screen:** round 1's line is still there in Drawing2.

→ **Ask the user to confirm** before round 3.

---

## Round 3 — reads of the non-active drawing

`acad_list_entities` was already proven against a non-active drawing in the first run; this
round covers the two reads that were not.

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_list_layers\",\"arguments\":{\"drawing\":\"Drawing1\"}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":6,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_list_entities\",\"arguments\":{\"drawing\":\"Drawing1\",\"limit\":3}}}"
```

Take one handle from that list as `<D1_HANDLE>`:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"drawing\":\"Drawing1\",\"handles\":[\"<D1_HANDLE>\"]}}}"
```

**Response:** all three succeed with `drawing` = `"Drawing1.dwg"`, `drawing_is_active` =
`false`. The layer list is **Drawing1's** layers, not Drawing2's. Log lines show
`is_active=False outcome=done`.

**On screen:** nothing changes; AutoCAD stays on Drawing2.

→ **Ask the user to confirm** before round 4.

---

## Round 4 — same handle, two drawings (reads only)

Use the round 1 line's handle `<R1_LINE>` (it exists in Drawing2):

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"drawing\":\"Drawing2\",\"handles\":[\"<R1_LINE>\"]}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_get_entity_properties\",\"arguments\":{\"drawing\":\"Drawing1\",\"handles\":[\"<R1_LINE>\"]}}}"
```

**Response:** the first is round 1's LINE. The second is **whatever that handle means in
Drawing1** — with 321 entities there, quite possibly an unrelated entity, or a failure entry.
Either is correct; record which. If it resolved to an entity, that is the collision the guard
exists for, demonstrated live.

→ **Ask the user to confirm** before round 5.

---

## Round 5 — the guard after a tab switch

1. In AutoCAD, **click over to Drawing1**.
2. Re-run round 1's call unchanged (it still says `"drawing":"Drawing2"`).

**Response:** refused — `"Drawing2.dwg" is not the active drawing … Active: Drawing1.dwg`.
**On screen:** nothing drawn in Drawing1. This is the guard doing its job: the caller's idea
of the drawing is stale, and the edit is blocked rather than redirected.

3. Same call **without** `drawing` → succeeds, `drawing` = `"Drawing1.dwg"`, and the line
   lands in **Drawing1**. Expected: with no guard, writes follow the active drawing.

Note whether requests are served at all while the drawing `MCPSTART` was run from is not
active — partly answered in the first run (reads worked), still unverified for writes.

→ **Ask the user to confirm** before round 6.

---

## Round 6 — selection errors, each `isError: true`

| `drawing` on `acad_list_entities` | Expect |
|---|---|
| `"Gamma"` | "No open drawing matches", lists both drawings, marks the active one |
| `"Drawing"` | same — a prefix is not a match |
| `""` | "must not be empty" |
| `3` | must be a string |

And on a write: `acad_draw_circle` with `"drawing":"Gamma"` must say **"No open drawing
matches"**, not "not the active drawing" — a typo must not be reported as a refusal.

---

## Round 7 — optional, observe only

With the server running, `CLOSE` the drawing `MCPSTART` was run from. Record what AutoCAD
does and whether `acad_ping` still answers. A finding, not a pass/fail.

---

## What to come back with

Write results to `tests/results/PHASE3B_DRAWINGS_RESULTS.md` (append a "Retest" section):

1. **Verdict** — writes refused cleanly on a non-active drawing? reads work on it?
2. **Environment** — AutoCAD build, DLL path and timestamp, AcadHotLoader vs direct NETLOAD, log level.
3. **Per-round results** with handles, and that the user visually confirmed each.
4. **Failures** — what was sent, what came back, log lines verbatim.
5. **Round 4 and 7 observations.**

Report, do not refactor.
