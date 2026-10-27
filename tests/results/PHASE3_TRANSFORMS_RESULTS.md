# Phase 3 live check — transforms and copy — Results

Live validation of `acad_transform` (move / rotate / scale / mirror, optionally as copy) and
`acad_copy` against a real AutoCAD 2026, per `tests/live/PHASE3_TRANSFORMS.md`. Instead of the
doc's line/circle/rectangle fixtures, the test subject was a 17-entity "Squirtle" drawing
(circles, arcs, polylines on four layers) plus one TEXT label. Each of rounds 1-4 was
confirmed on screen by the user; round 4 with a screenshot.

## 1. Verdict

**Both tools work end to end for move, rotate, scale, mirror, copy rows and every error guard
tested.** Rounds 5 (attributed blocks) and 6 (units other than mm) were **not run**; see
section 4.

## 2. Environment

- AutoCAD 2026, live session, two drawings open; all calls targeted `Drawing2.dwg` (active),
  `INSUNITS` = 4 (mm). Multi-drawing build of the server (Phase 3b), so every call passed
  `drawing`.
- AutoCAD build, DLL path and timestamp: not recorded. Log level Info, so no log lines quoted.
- Test geometry placed at x 2800-6500, clear of existing content in Drawing2.

## 3. Per-round results

| Round | Result | Detail |
|---|---|---|
| 1 rotate | **pass** | 17 entities, 90 deg about base (3400,0), `copy:true`. Head (4000,430) -> (2970,600); left eye (3955,445) -> (2955,555); tail arc 180/90 -> 270/180. Counter-clockwise, degrees, about Z. User confirmed on screen. |
| 2 move + scale | **pass** | Move (0,0)->(0,-600), 17 entities. Scale 0.5 about (2970,0): head stays centred at (2970,0), radius 110 -> 55; body centre slides to (3100,0), radius 75. User confirmed. |
| 3 copy row | **pass** | `acad_copy` count 3, displacement (500,0): offsets 500/1000/1500, 51 new entities, 17 distinct new handles per copy (`549`-`57B`). User confirmed four Squirtles in a row. |
| 4 mirror copy | **pass** | 17 entities + TEXT "SQUIRTLE" mirrored about x=5900, `copy:true`: 18 new handles in input order, `mirrored_text_count` 1. Head -> (6300,430); text anchored at 6400, reads backwards (screenshot). Matches documented behaviour. |
| 4b mirror in place | **not run** | `copy` defaults to false was exercised by move/scale, not by mirror. |
| 5 attributed block copy | **not run** | See 4. |
| 6 units (INSUNITS 6) | **not run** | See 4. |
| 7 guards | **pass** | All refused, nothing changed: duplicate handle ("more than once"); scale + `deg_angle` (names `deg_angle`); rotate without angle; identical mirror points; unresolvable handle `ZZZZ` ("nothing was changed"); zero displacement; `count` 101 ("between 1 and 100"). |
| 7 locked layer | **pass** | `acad_transform` move and `acad_copy` on a line on locked `P3_LOCKED`: both `isError`, "unlock the layer with acad_set_layer_state". The predicted copy refusal held. |

## 4. Not run

- **Round 5 — copying an attributed block.** Needs a block with an attribute (`ATTBLK`,
  `TAG`=`KEEP`), which cannot be built through the MCP yet. **Decision: moves to Phase 4,
  which adds block and attribute tools.** Phase 4 must include a live check that `acad_copy`
  (DeepCloneObjects) keeps the attribute value, and that a shallow clone or a reset to the
  default (`A1`) does not happen. This is the bug the switch away from `GetTransformedCopy`
  was meant to prevent, so it is still **unverified live**.
- **Round 6 — units.** Needs `INSUNITS` set to 6 and back by hand. Expected: a move of
  (1000,1000) mm reports `mm_per_drawing_unit` 1000 and the line lands at (1,1)-(2,1) drawing
  units; a copy of 500 mm lands at 1.5 units. Only mm drawings were exercised, so unit
  conversion in transform and copy is **unverified live**.
- **Round 4b — mirror without `copy`.** Trivial; run with round 6.

## 5. Observations

- Mirrored and rotated entities carry Z of about 1e-14 (float noise); harmless.
- Rotated arc angles are reported as the original plus the rotation (e.g. 270/180).
- Every call was all-or-nothing, including when one handle was bad.
- The multi-drawing rule from `PHASE3B_DRAWINGS_RESULTS.md` applied throughout: writes work
  because the target was the active drawing.

## 6. Leftovers in Drawing2

Empty layer `P3_LOCKED` (no layer-delete tool; `PURGE` removes it); the sideways half-size
Squirtle, the row of copies and the mirrored copy; layers `SQ_BLUE`, `SQ_BELLY`, `SQ_SHELL`,
`SQ_EYES`. Drawing1 holds the original Squirtle. The test line `521` in Drawing2 was deleted.
