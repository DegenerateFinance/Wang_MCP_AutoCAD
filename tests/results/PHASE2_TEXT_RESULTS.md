# Phase 2 live check — text tools — RESULTS

## 1. Verdict

`acad_add_text`, `acad_add_mtext` and `acad_set_text_content` work end to end. One bug found
and fixed live: **a text style's fixed height was not applied** (round 7). Justified text is
stored correctly but did not repaint in place (round 2, display only).

## 2. Environment

- AutoCAD 2026, port 5599, 31 tools. Driven through the MCP tools directly.
- Rounds 1–8: `Y:\ACAD_DLL\Wang_MCP_AutoCAD.dll` 18:59 (HEAD threading + the Phase 4
  `convert_block_to_static` fix), pid 15108. Round 7 retest: 19:31 build with the fixed-height
  fix, pid 13324. Both via AcadHotLoader. Log level Info.
- Drawing3.dwg, mm drawing. Everything placed at x ≥ 10000 to stay clear of the Phase 4 blocks.

## 3. Per-round results

| Round | Result | Handles | Detail |
|---|---|---|---|
| 1 plain text | **PASS**, user confirmed | `60D` | `justify` left, `mm_height` 10, `height_overridden_by_style` false |
| 2 justification | **PASS (stored) / display issue** | `60F` CENTRED + circle `60E`; `611` TOPRIGHT + circle `610` | MiddleCenter / TopRight stored. On screen neither was drawn at its circle; the user found the justification set correctly but the view not updating — a display refresh issue, not a tool failure. Round-trip `mm_position` is the alignment point itself ([10200,100]), not a derived baseline point |
| 3 mtext wrap + escaping | **PASS**, user confirmed | `612`, `613` | Wrap: `wrapped` true, `mm_actual_width` 137.8, `mm_actual_height` 34.8. Escaping: `contents` `Path C:\\\\Temp\\\\Job\\{01\\} 50%%`, `wrapped` false, displayed literally on one line |
| 4 set content + guards | **PASS**, user confirmed | `60D`, `612` | `changed` 2, `text_entities` 1, `mtext_entities` 1; `60D` stayed baseline-left at (10000,0). [`60D`,`ZZZZ`] refused naming `ZZZZ`, nothing changed. Circle `60E` refused as `CIRCLE`, naming TEXT/MTEXT/ATTRIB |
| 5 units | **DROPPED** | — | Only mm drawings are in scope; removed from the brief |
| 6 bad input | **PASS** | — | `mm_height` −5, whitespace text, line break (points to `acad_add_mtext`), `justify` "centre" (lists the 12 names), unknown style (suggests STYLE), unknown layer (suggests `acad_create_layer`), mtext `mm_width` −5 (says 0 = no wrap) — all clean `isError`, AutoCAD unharmed |
| 7 fixed-height style | **FAIL → FIXED**, user confirmed | `617`, `63E`, `643` (before); `651` (after) | Style `FIXED10` (height 10, later made bold by the user), `mm_height` 50. Before: response `height_overridden_by_style` true but `mm_height` 50, drawn 50 high. After the fix: `mm_height` 10, drawn 10 high and bold |
| 8 round-trip | **PASS** | `60D` `60F` `611` `612` `613` `614` | text, height, rotation 0, justify/attachment and positions all match what was written |
| TODO: arc angles | **PASS**, user confirmed | `614` | Arc 0°→90°: read back `deg_start_angle` 0, `deg_end_angle` 90, `deg_total_angle` 90 — not 5156 |

## 4. Failures

**Round 7 — fixed style height ignored. FIXED.** `acad_add_text` assumed AutoCAD enforces a
style's non-zero `TextSize` by itself. It does not: only the UI does (the TEXT command, or
picking the style in Properties — the user confirmed re-picking FIXED10 by hand updated the
height). Setting `TextStyleId` through the API keeps the `Height` given. Font changes to the
style (bold) do follow automatically, since they are not stored per entity. Fix
(`TextTools.cs`): when the style has a fixed height, set `Height` to it. `acad_add_mtext` does
not read the style height and does not claim to; unchanged.

## 5. Other observations

- Single-line TEXT (and tool-created INSERTs) report `mm_min`/`mm_max` `null`; MTEXT and ARC
  have extents.
- `acad_draw_circle` still returns a `du_radius` field next to `mm_radius`.
