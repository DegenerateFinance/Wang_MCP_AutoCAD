# MCP listener — test brief

You are validating the first working slice of an MCP server that runs **inside** AutoCAD
2026's own process. It has never been exercised against a live AutoCAD. Everything that can
be tested without AutoCAD already passes (84 unit tests, plus a real-socket HTTP handshake
driven with `curl`); everything that needs a drawing is unverified. That is your job.

**You need a live AutoCAD 2026 session with a drawing open.** The load steps happen at
AutoCAD's command line, by hand. If you cannot reach a running AutoCAD, stop and say so —
do not simulate the results.

---

## 1. What this is

An in-process plugin (`Wang_MCP_AutoCAD.dll`, loaded via `NETLOAD`) that hosts an HTTP
listener inside AutoCAD and exposes AutoCAD's managed API as MCP tools.

| | |
|---|---|
| **HTTP server** | `System.Net.HttpListener`, in-process. No separate host process, no ASP.NET, no NuGet packages. |
| **Bound prefix** | `http://127.0.0.1:5599/` — the root is registered and paths are routed in code. Loopback only. |
| **MCP endpoint** | `http://127.0.0.1:5599/mcp` (`/mcp/` with a trailing slash also works) |
| **Health probe** | `GET http://127.0.0.1:5599/health` — plain JSON, not JSON-RPC |
| **Port** | **5599, fixed.** No scanning, no fallback. If it is taken the server refuses to start and says so. |
| **Protocol** | MCP Streamable HTTP, JSON responses only (no SSE). `protocolVersion` `2025-06-18`. |
| **Threading** | One dedicated background thread parked in `GetContext()`, requests handled inline (serialised). |
| **Auth** | None — loopback only. A request whose `Origin` header is present and **not** loopback gets `403`. |
| **Start** | **Manual.** Loading the plugin binds nothing; you run `MCPSTART`. |

### Logs

```
%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log
= C:\Users\ACER-IDESAI-RCH\AppData\Local\Wang_MCP_AutoCAD\mcp.log
```

Default level is `Info`. **Turn it up to `Debug` before you start** — the `MCPLOG` command
prints the path and prompts for a level. The log is your primary evidence; quote from it.

Line format is `timestamp LEVEL [thread] key=value ...`. **The `[ n]` thread-id column
matters:** for a drawing tool you should see the HTTP request line on one thread id and the
transaction line on a *different* one. That difference is the cheapest proof that the
document-thread marshalling actually happened rather than being silently skipped.

---

## 2. Loading it

### Where the DLL is

```
%LOCALAPPDATA%\Wang_MCP_AutoCAD\deploy\Wang_MCP_AutoCAD.dll
```

> **Trap — read this.** There is a **stale** copy at
> `C:\Hyper-Shared\ACAD_DLL\Wang_MCP_AutoCAD.dll` from an older build. Picking it loads old
> code that silently lacks everything below. The deploy location moved recently and
> `CLAUDE.md` still names the old path — `CLAUDE.md` is wrong on this point. Confirm the
> file you pick has today's timestamp and is ~60 KB. The `WANG_MCP_DEPLOY_DIR` environment
> variable overrides the location if it is set.

### Dev loop (expected)

1. `NETLOAD` → `Wang_MCP_AutoCAD.Loader.dll`, **once per AutoCAD session**.
2. `MCPRELOAD` → file picker → choose `Wang_MCP_AutoCAD.dll` from the deploy folder above.
3. `MCPINVOKE` → pick `MCPSTART`.

**Step 2 is itself a test.** It must report `(1 IExtensionApplication(s) initialized)`.
Before this change it reported `0` — the plugin had no entry point at all. If you see `0`,
you loaded the stale DLL; go back and check the path.

**Why step 3 is indirect:** AutoCAD only auto-registers `[CommandMethod]`s for assemblies it
NETLOADed itself. Under `MCPRELOAD` the plugin is loaded into a collectible
`AssemblyLoadContext` instead, so its commands are invisible at the command line.
`MCPINVOKE` is the Loader's reflection-based stand-in. It will list the plugin's commands —
`MCPSTART`, `MCPSTOP`, `MCPSTATUS`, plus older throwaways (`MCPHELLO`, `MCPGREET`,
`MCPLOG`, `MCPCircleAndConcentricSquare`). Note `MCPSTART`/`MCPSTOP`/`MCPSTATUS` share the
prefix `MCPST`, so type the full name at the keyword prompt.

If the Loader's picker opens in the wrong folder, the deployed Loader binary predates a
recent source change — navigate manually rather than rebuilding it.

### Production path (also worth one check)

`NETLOAD` `Wang_MCP_AutoCAD.dll` directly. Commands then register normally and `MCPSTART`
is typed straight at the command line, no `MCPINVOKE`. Do this **last**, in a fresh AutoCAD
session — a direct `NETLOAD` cannot be undone and will block the dev loop for that session.

---

## 3. Commands

| Command | Lives on | Reached how (dev loop) |
|---|---|---|
| `MCPRELOAD`, `MCPUNLOAD`, `MCPINVOKE` | Loader | typed directly |
| `MCPSTART`, `MCPSTOP`, `MCPSTATUS` | plugin | via `MCPINVOKE` |
| `MCPLOG` | plugin | via `MCPINVOKE` |

`MCPSTATUS` prints the URL, pid, request/failure counts, average ms/request and the log path.

---

## 4. The tools

All lengths are **millimetres** on the way in and out. The server converts to the drawing's
own units using its `INSUNITS` system variable and echoes `mm_per_drawing_unit` in every
geometry response so the conversion is auditable.

### `acad_ping`
No arguments. Does **not** touch the document — works even with no drawing open. Returns
server name/version, pid, port, url, uptime, counters, log path and level.

### `acad_draw_circle`
| Arg | Required | Notes |
|---|---|---|
| `mm_center_x` | yes | number |
| `mm_center_y` | yes | number |
| `mm_center_z` | no | defaults to 0 |
| `mm_radius` | yes | must be > 0 |
| `layer` | no | **must already exist**; defaults to the current layer |

Returns `handle`, `layer`, `mm_center`, `mm_radius`, `du_radius`, `mm_per_drawing_unit`,
`insunits`.

### `acad_list_entities`
| Arg | Required | Notes |
|---|---|---|
| `layer` | no | exact, case-insensitive filter |
| `limit` | no | 1–1000, default 100 |

Returns `drawing`, `totalMatched`, `returned`, `truncated`, `mm_per_drawing_unit`,
`insunits`, and `entities[]` of `{handle, type, layer, mm_min, mm_max}` plus `mm_radius` for
circles.

---

## 5. What to test

Use `curl.exe` explicitly — in PowerShell, bare `curl` is an alias for `Invoke-WebRequest`
and takes different arguments.

### 5.1 Handshake (should be boring — this part is already proven off-AutoCAD)

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2025-06-18\",\"capabilities\":{},\"clientInfo\":{\"name\":\"curl\",\"version\":\"1\"}}}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/list\"}"

curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_ping\",\"arguments\":{}}}"
```

`tools/list` must show three tools. If `acad_ping` works but the drawing tools do not, the
fault is in the document-thread hop, not the protocol.

### 5.2 Draw something — the main event

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":4,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_draw_circle\",\"arguments\":{\"mm_center_x\":0,\"mm_center_y\":0,\"mm_radius\":50}}}"
```

Then **look at AutoCAD**. `ZOOM` → `Extents`. Confirm with your own eyes that a circle
appeared, and that it is the size the response claims. Report the returned `handle`.

Draw two or three more at different centres, radii and on different layers. Then:

```bash
curl.exe -s -X POST http://127.0.0.1:5599/mcp -H "Content-Type: application/json" ^
  -d "{\"jsonrpc\":\"2.0\",\"id\":5,\"method\":\"tools/call\",\"params\":{\"name\":\"acad_list_entities\",\"arguments\":{\"limit\":50}}}"
```

Confirm every handle you drew comes back, with plausible `mm_min`/`mm_max` bounds. Then
filter by a layer and confirm the filter actually narrows the result.

### 5.3 Units — the one most likely to be wrong

This is the point of the whole `mm_`/`du_` split, so test it properly.

1. In a drawing with `INSUNITS` = 4 (millimetres), draw `mm_radius: 1000`. Expect
   `du_radius` **1000** and `mm_per_drawing_unit` **1**.
2. Set `INSUNITS` to 6 (metres). Draw `mm_radius: 1000` again. Expect `du_radius` **1** and
   `mm_per_drawing_unit` **1000**.
3. **Measure both circles in AutoCAD.** They must be the same physical size. If they are
   not, the conversion is backwards — that is the single most valuable bug you could find.
4. Set `INSUNITS` to 0 (unitless). Expect a clean `isError: true` whose message names
   `INSUNITS` and tells you to set it. It must **not** silently assume millimetres.

### 5.4 Bad input — should be `isError`, never a crash

Each of these must return HTTP 200 with `isError: true` and a message that names the
offending field, so a model could read it and retry:

- `mm_radius: -5` and `mm_radius: 0`
- `mm_center_x` omitted entirely
- `mm_center_x: "0"` (a string instead of a number)
- `layer: "DOES_NOT_EXIST"`
- `limit: 5000` on `acad_list_entities` (range is 1–1000)

By contrast, a tool name that does not exist should be a **JSON-RPC error `-32602`**, not
`isError`. That distinction is deliberate; confirm both halves.

### 5.5 Busy AutoCAD — the timeout path

1. At AutoCAD's command line type `LINE` and **leave it waiting for a point**.
2. Now `curl` `acad_draw_circle`.
3. Expect: roughly **15 seconds**, then `isError: true` saying AutoCAD did not become
   available and is probably busy with a command or dialog. AutoCAD itself must be
   unharmed — the pending `LINE` still live, no crash, no dialog.
4. Press `Esc` to cancel the `LINE`. Check the log for `op=gateway/call outcome=timeout`.
5. **Then confirm the circle does not appear afterwards.** A timed-out call that draws
   anyway, seconds after the client was told it failed, is a real bug this design guards
   against — verify the guard works.

### 5.6 Reload hygiene — the one that protects every future session

With a modal command still pending (as in 5.5), run `MCPUNLOAD` or `MCPRELOAD`.

- It must return **promptly** — not stall for 15 seconds.
- The log should show `op=gateway/abort` and/or `outcome=abandoned`.

Then, with nothing pending, do `MCPSTOP` → `MCPRELOAD` → `MCPSTART` **three times**:

- Every stop must write `op=listener/shutdown port=5599 requests=… failures=… ms_request_avg=…`
- The port must stay **5599** every time. If a later start fails to bind, a previous
  listener thread leaked and is still holding the port — that is a serious finding.
- `op=listener/join outcome=timeout … note=alc_will_leak` in the log is also a serious
  finding. Report it verbatim.

### 5.7 Guards

```bash
curl.exe -s -i -X POST http://127.0.0.1:5599/mcp -H "Origin: http://evil.example" ^
  -H "Content-Type: application/json" -d "{\"jsonrpc\":\"2.0\",\"id\":9,\"method\":\"ping\"}"
```

Expect `403`. `Origin: http://localhost:3000` and no `Origin` at all must both be `200`.
`GET /mcp` → `405`. An unknown path → `404`.

### 5.8 Optional — a real MCP client

If you can, point an actual MCP client (the MCP Inspector, or Claude Code with an HTTP MCP
server configured) at `http://127.0.0.1:5599/mcp` and call the tools through it. Whatever a
real client complains about is the true conformance gap; `curl` will not find it.

---

## 6. What to come back with

Report, do not refactor. If you fix something trivial on the way, say exactly what you
changed. Structure your reply as:

1. **Verdict** — one line: does the slice work end to end, yes or no?
2. **Environment** — AutoCAD build, the DLL path and timestamp you actually loaded, whether
   the dev loop or a direct `NETLOAD` was used, and the drawing's `INSUNITS`.
3. **Results table** — one row per section 5.1–5.7: pass / fail / not run, with the
   evidence. For drawing steps include the returned `handle` and confirm you *visually*
   verified the geometry in AutoCAD.
4. **Failures** — for each: what you sent, what came back, the relevant log lines quoted
   verbatim, and what you saw happen in AutoCAD.
5. **Marshalling evidence** — the log lines for one `acad_draw_circle` call, showing the
   differing `[ n]` thread ids.
6. **Shutdown evidence** — the three `op=listener/shutdown` summary lines from 5.6.
7. **Anything surprising** — AutoCAD dialogs, freezes, things this brief did not predict.

Negative results are the valuable ones here. Nothing in sections 5.2 through 5.6 has ever
run against a real AutoCAD, so a clean pass on all of them would be a slightly suspicious
outcome — if everything passes first time, say so plainly but double-check 5.3 step 3 and
5.5 step 5, which are the two easiest to mistake for a pass.
