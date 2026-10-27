# Phase 3b live check — several open drawings — Results

Live validation of multi-drawing targeting against AutoCAD 2026, per
`tests/live/PHASE3B_DRAWINGS.md`. Only rounds 1 (listing) and 2 (non-active drawing) were run;
round 2 decided the design, so the rest were not needed.

## 1. Verdict

**A running `MCPSTART` can read a non-active drawing but cannot write to it.** AutoCAD rejects
the write with `WrongDatabase`. The documented fallback (`CommandFlags.Session`) is **not
pursued**. Instead this is adopted as a **design rule: the MCP edits only the active drawing.**
That fits how the tool is used (one drawing being worked on at a time) and removes the whole
class of "which drawing did that land in" mistakes.

## 2. Environment

- AutoCAD 2026, live session, two drawings open, both `INSUNITS` = 4:
  `Drawing1.dwg` (inactive) and `Drawing2.dwg` (active), both under
  `C:\Users\IDESAI-DD\Documents\Wang_AutoCAD_MCP\`.
- Server `wang-mcp-autocad` 1.0.0, port 5599, pid 7456, log level Info (not Debug, so no
  `op=gateway/run` lines were captured).
- AutoCAD build and DLL timestamp: not recorded.

## 3. Results

| Round | Result | Detail |
|---|---|---|
| 1 listing | pass | `acad_list_drawings`: count 2, active `Drawing2.dwg`, paths correct, `ambiguous_name` false, `insunits` 4, `mm_per_drawing_unit` 1 |
| 1 plain draw | not run | would have added geometry to the user's drawing |
| 2 write to non-active | **refused** | `acad_draw_line` with `drawing:"Drawing1"` -> `isError`, `AutoCAD rejected the line: WrongDatabase.` Nothing drawn. |
| 2 read of non-active | pass | `acad_list_entities` with `drawing:"Drawing1"` -> 321 entities, `drawing_is_active:false`. Drawing2 -> 1 entity. |
| 3-6 | not run | 3 (handle collision) is still worth running, reads only; 4-6 not needed |

## 4. Decision: edit the active drawing only

- **Writes** (draw, copy, delete, transform, layer create/set/state, text add/edit,
  property set): act on the active drawing only.
- **Reads** (`acad_list_entities`, `acad_get_entity_properties`, `acad_list_layers`,
  `acad_list_drawings`): may still take `drawing` and work on any open drawing. Proven for
  `list_entities`; the other two reads are untested.
- A write that names a non-active drawing must fail **before** reaching AutoCAD with a clear
  message, not the raw `WrongDatabase`.
- Every response keeps naming its drawing, since the active drawing can change under the
  caller between calls.

## 5. Server changes needed (source is not in this workspace)

1. In the gateway, for write tools, if the resolved drawing is not the active one, return
   `isError` with e.g.: `"Drawing1.dwg" is not the active drawing. This server edits the active
   drawing only; switch to it in AutoCAD, then retry. Active: Drawing2.dwg.` Log
   `outcome=refused_not_active`.
2. Either drop the `drawing` property from write-tool schemas, or keep it as a **guard**: if
   given, it must match the active drawing, otherwise refuse. The guard is the safer option,
   since it stops a caller that believes it is on drawing X from editing drawing Y.
3. Keep `drawing` on read tools. Update their descriptions to say reads work on any open
   drawing and writes need the active one.
4. Update the `acad_list_drawings` description likewise.
5. Unit tests: write + non-active -> refusal with no gateway call to AutoCAD; write + active
   -> ok; read + non-active -> ok; guard mismatch -> refusal.

## 6. Retest after the server change — pass

Run later against the same two drawings (Drawing1 / Drawing2):

| Check | Result |
|---|---|
| Write naming the non-active drawing | **pass.** Refused before AutoCAD: `"Drawing1.dwg" is not the active drawing, so nothing was changed. This server edits the active drawing only… Active: Drawing2.dwg. Read-only tools can still read "Drawing1.dwg"…` |
| Write naming the active drawing | **pass.** Line drawn, response `drawing_is_active:true`. |
| Guard after the user switched drawings mid-session | **pass.** `delete_entities` on Drawing2 was refused once Drawing1 became active. |
| Read of non-active drawing | **pass.** `list_entities` and `get_entity_properties` on Drawing1 worked, `drawing_is_active:false`. |
| Handle collision (round 3) | **demonstrated.** Handles `526`/`527` exist in both drawings as different entities (Drawing1: eye pupil and smile arc; Drawing2: head and body). Always pass `drawing`. |
| Phase 3 transforms and copy under the new rule | **pass.** See `PHASE3_TRANSFORMS_RESULTS.md`. |

## 7. Open items

- ~~Retest after the change~~ — done, section 6.
- ~~Round 3, same handle in two drawings~~ — demonstrated, section 6.
- The ambiguity checks (two open drawings with the same file name) are still unverified live.
- Closing the drawing that owns the running `MCPSTART` is unobserved.
- Round 4 is partly answered: if the user switches drawings, `drawing` omitted follows the
  active one, and writes then land there. Whether requests are served at all while the server's
  own drawing is inactive is unverified, but reads on Drawing1 worked while Drawing2 was active.
