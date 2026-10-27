# AGENTS.md

This file provides guidance to coding agents when working with code in this repository.

## What this is

An in-process AutoCAD .NET plugin (loaded via `NETLOAD`) that hosts an HTTP listener inside AutoCAD's own process, exposing AutoCAD's managed API (ObjectARX-based, full AutoCAD — not LT) as HTTP endpoints. The goal is to let an external process (MCP server, LLM client, or any other tool) drive AutoCAD over HTTP without keystroke simulation or file polling.

Endpoints should map to logical operations (create entity, query entities, layer management, block insert with attributes, save/open, etc.) — design the surface area deliberately; do not just expose a passthrough for arbitrary AutoCAD command strings.

The project is a very early scaffold: `Wang_MCP_AutoCAD/TestFunc.cs` holds a few throwaway `[CommandMethod]`s used to prove the load path works, plus `Log.cs`. There is no HTTP listener, plugin entry point, or endpoint routing implemented yet.

## Build

```
dotnet build Wang_MCP_AutoCAD\Wang_MCP_AutoCAD.csproj
```

Build the individual project, not the solution (`dotnet build Wang_MCP_AutoCAD.slnx`). Three projects: `Wang_MCP_AutoCAD` (the actual plugin — this is what production NETLOADs), `AcadHotLoader` (a dev-only, plugin-agnostic hot-reload shim — see below), and `Wang_MCP_AutoCAD.Tests`. The first two have an `AfterTargets="Build"` MSBuild target that runs `deploy.ps1` to copy their output to the deploy folder (`%WANG_MCP_DEPLOY_DIR%`, default `%LOCALAPPDATA%\Wang_MCP_AutoCAD\deploy`), so a solution build also redeploys the hot loader — which is rarely wanted, since it changes rarely and is already NETLOADed into the running AutoCAD session. Build `AcadHotLoader\AcadHotLoader.csproj` explicitly on the rare occasions it changed.

```
dotnet test Wang_MCP_AutoCAD.Tests\Wang_MCP_AutoCAD.Tests.csproj
```

`Wang_MCP_AutoCAD.Tests` (xunit) `ProjectReference`s the plugin, so **running the tests also rebuilds and redeploys the plugin** — harmless in the dev loop (AcadHotLoader reads the DLL into memory and holds no handle), but the copy will fail while an AutoCAD session has NETLOADed `Wang_MCP_AutoCAD.dll` directly.

The test project targets `net8.0-windows`/`x64` to match the plugin; a `net8.0`/AnyCPU test project cannot reference it. The plugin's AutoCAD references are `<Private>false</Private>`, so **no AutoCAD DLL is ever copied into the test output** — deliberately. Anything reachable from a test must be free of AutoCAD types; a `FileNotFoundException` for `acmgd`/`acdbmgd` in a test run means protocol code and database code have grown together and need splitting, not that the test project needs AutoCAD references. Keep JSON-RPC parsing, routing, tool-schema construction, and payload/unit conversion on the testable side of that line.

No lint configuration exists yet.

## Live verification: your job is build + brief, nothing else

Only a human with a live AutoCAD 2026 session can confirm geometry actually appeared. You cannot see the screen, so **never claim a live result and never simulate one.** Your half of the loop is exactly two steps:

1. **Build.** `dotnet build Wang_MCP_AutoCAD\Wang_MCP_AutoCAD.csproj`. The `AfterTargets="Build"` target runs `deploy.ps1`, which copies the DLL to the deploy folder for you.
2. **Write a brief** for the testing agent as a markdown file, then hand over and stop.

**Do not verify the copy.** `deploy.ps1` prints `Copied Plugin to <path>` and fails the build if it cannot. Confirming the deploy folder's timestamps, hashing the DLL, diffing it against `bin\`, or reflecting over the deployed assembly to prove a new type is in it are all wasted steps — a successful build already means the right bytes are in place. Trust the script.

### What the brief looks like

Concise and runnable, not a design document. Per round:

- The exact `curl.exe` calls to run, copy-pasteable, with the arguments already filled in.
- What the JSON response should contain — the specific fields and plausible values, not prose.
- **What the tester should expect to SEE in AutoCAD**, in one or two lines, phrased so a wrong result is obvious ("text centred *on* the point, not starting at it"; "at the origin instead would mean the AlignmentPoint trap fired").
- Then **stop and wait for the user's visual confirmation** before the next round.

Rinse and repeat: one small round, response check, visual confirmation, next round. Order rounds so an early failure explains the later ones, and put the checks that can only fail live — unit conversion, angle conventions, text alignment — before the ones the unit tests already cover.

Keep these briefs in `tests/live/`, one per phase. Say plainly which checks have never been run against a real AutoCAD; a clean pass on everything first time is a suspicious outcome worth double-checking, not a result to celebrate.

**Briefs go stale, and a stale brief is worse than none** — it sends the tester to verify behaviour that no longer exists. When you change the architecture, correct the affected brief in the same pass. `MCP_TEST_BRIEF.md` is the cautionary example: it still documents 3 tools, a background listener thread, a 15-second gateway timeout and `op=gateway/abort` log lines, none of which survived `9eea7cf`, and it warns the tester away from `C:\Hyper-Shared\ACAD_DLL` as "stale" when `deploy.local.props` now makes that the real deploy target.

## Code style

- **No `var`** — always use explicit types. `string path = "foo"`, not `var path = "foo"`. This includes AutoCAD types: `Document doc = ...`, `Transaction tr = ...`, `PromptPointResult centerResult = ...`.
- **Early returns** — guard clauses at the top, no deep nesting. The `if (result.Status != PromptStatus.OK) { return; }` shape after every prompt is the canonical example.
- **Long signatures** — when a method has more than 4 parameters, put each parameter on its own line. At the call site, use named arguments (`paramName: value,`) each on its own line.
- Private fields: `_camelCase`. Public members: `PascalCase`.
- **Time-valued members must carry their unit as a prefix on the unit part of the name** — `Ms_RequestTimeout`, `Sec_ListenerShutdown`, `Microsec_Elapsed`. Never a bare `Timeout` or `Elapsed`.
- **All measurement members must carry their unit in the name** — `Mm_Width`, `Deg_Rotation`, `Kg_Weight`. AutoCAD-specific wrinkle: geometry read from the database is in *drawing units*, which are not implicitly millimetres. Name an unconverted database value `Du_Radius`, and only use `Mm_` once an explicit conversion has happened. Endpoint payloads must state their unit for the same reason.
- **Always use `Stopwatch` to measure elapsed time** — never `DateTime.Now`/`DateTimeOffset.Now` subtraction. `Stopwatch.StartNew()` before the operation, `sw.Stop()` after, then `sw.ElapsedMilliseconds` or `sw.Elapsed.TotalMicroseconds`. `DateTime` is only correct for wall-clock *timestamps* — e.g. the timestamp `Log.Write` stamps on each line, which is compliant and should not be "fixed" to `Stopwatch`.

### Error handling

- **Never write an unqualified `Exception` in this codebase.** Any file with `using Autodesk.AutoCAD.Runtime;` — which is most of them, since that's where `[CommandMethod]` lives — binds bare `Exception` to `Autodesk.AutoCAD.Runtime.Exception`, silently narrowing the catch to AutoCAD's own error type and letting every BCL exception escape. Always spell `System.Exception` explicitly; `HotLoader.cs` and `TestFunc.cs` already do.
- Catch the most specific type first: `Autodesk.AutoCAD.Runtime.Exception` (carries the `ErrorStatus` — log it) → `System.Exception`.
- Wrap every AutoCAD transaction, every file operation, every network/listener operation, and every math or string parse in a `try/catch`.
- Always pass the exception object itself to `Log.Error`/`Log.Warn` — `Log.Write` appends the full tree (message, inner exceptions, stack trace) from `ex`. Never pre-format `ex.Message` into the string.
- Fallible methods return `null` or `bool` rather than throwing; callers guard with early-return or `continue`. Exception: a `[CommandMethod]` body should rethrow after logging so AutoCAD still reports the failure to the user.

### Logging

Logging goes to a file via `Log` (`Wang_MCP_AutoCAD/Log.cs`), not `Editor.WriteMessage` — see the architecture section below for why. Conventions:

- **Write `key=value` pairs, lowercase snake_case keys** — `Log.Info($"cmd=mcphello elapsed_ms={sw.ElapsedMilliseconds}")`. **Deviation worth knowing:** this is not structured logging. `Log` takes a plain string, so interpolation *is* the mechanism rather than something to avoid — there is no template engine to capture properties. The `key=value` discipline is what keeps lines greppable in its place. If `Log` ever grows a real template overload, this rule flips back to "never interpolate into the template".
- **Every log line must name its own operation** — `op=tools/call tool=create_circle`. Inverted from the usual `ILogger<T>` rule: there is no category stamp here, no `{SourceContext}`, so a message that omits the operation is unattributable.
- Levels: `Debug` for attempt/entry, `Info` for success with key metrics, `Warn` for expected-but-notable misses (prompt cancelled, entity not found), `Error` for caught exceptions and for unhandled escapes at the top of a request or command.
- **Every AutoCAD transaction log entry must include**: the handle(s) or `ObjectId`(s) touched, `elapsed_ms` from a `Stopwatch`, and the entity count where applicable.
- **Every HTTP request must log** the tool/endpoint name, `elapsed_ms`, and the outcome — one line per request, at `Info` on success and `Error` on failure.
- The listener thread logs a summary line on shutdown: request count, failure count, total ms, average ms/request.

## Target environment constraints (non-obvious, verified by hand)

- **AutoCAD version: 2026.** The referenced managed DLLs (`AcCui.dll`, `accoremgd.dll`, `acdbmgd.dll`, `acmgd.dll`) are copied from `C:\Program Files\Autodesk\AutoCAD 2026\`. Their assembly version reports `25.1.0.0` — that is AutoCAD 2026's *internal* version number (not a mismatch with AutoCAD 2025).
- **TargetFramework must be `net8.0-windows`, Platform must be `x64`.** AutoCAD 2026 hosts a .NET 8 CLR and is 64-bit only. A project targeting a different TFM (e.g. `net10.0`) or `AnyCPU` will fail to load via `NETLOAD` or throw a platform-architecture mismatch — this was hit and fixed once already; don't regress it.
- **All `AutoCAD_DLL/*.dll` references use `<Private>false</Private>`** in both `Wang_MCP_AutoCAD.csproj` and `AcadHotLoader.csproj`. This is intentional: at runtime the plugin must bind against AutoCAD's already-loaded in-process copies of these assemblies, not a second copy shadowed into `bin/`. Do not remove `Private=false` when adding new AutoCAD references.

## NETLOAD can't reload a changed assembly — dev-time hot reload via `AcadHotLoader`

**Root cause:** `NETLOAD` loads the DLL via `Assembly.LoadFrom`, which lands it in .NET's default `AssemblyLoadContext`. That context refuses to load a second assembly with the same *simple name* — regardless of version — and default-context assemblies can never be unloaded. Bumping `AssemblyVersion` (which `Wang_MCP_AutoCAD.csproj` still does, via `<AssemblyVersion>1.0.*</AssemblyVersion>` + `<Deterministic>false</Deterministic>`) does **not** fix this; neither does renaming the deployed *file* (tried first, in `deploy.ps1` — doesn't help because the collision key is the assembly name embedded in the DLL, not the path). The only way to truly reload changed code is to never let AutoCAD's own NETLOAD touch it more than once per session.

**The fix — two deployment modes:**
- **Production:** NETLOAD `Wang_MCP_AutoCAD.dll` directly, same as any ordinary AutoCAD plugin. It's a fully self-contained plugin assembly with no awareness of the hot loader.
- **Development:** NETLOAD `AcadHotLoader_<stamp>.dll` once per AutoCAD session instead. Running the `HOTLOAD` command pops the same kind of file-picker NETLOAD itself uses (`Editor.GetFileNameForOpen`, opening in the hot loader's own folder, which is the deploy folder) — pick `Wang_MCP_AutoCAD.dll` — reads it as raw bytes, and loads it into a fresh **collectible** `AssemblyLoadContext` (`PluginLoadContext.cs`), which *can* be unloaded. Each `HOTLOAD` unloads the old context and loads the freshly-picked copy, so it is both the first load and every reload; `HOTUNLOAD` just unloads. Loading works by mirroring exactly what NETLOAD itself does otherwise: it scans for `IExtensionApplication` implementations and calls `Initialize()`/`Terminate()` on them. `PluginLoadContext` shares any assembly already resident in the default context (AutoCAD's own DLLs, BCL types) instead of duplicating it, so plugin code and host code agree on type identity. (A first version hardcoded the plugin's path instead of prompting; that hit a transient "file not found" — `File.Exists` swallows `UnauthorizedAccessException` and returns `false`, so a hardcoded path masks real errors — the file dialog sidesteps this since the OS won't let you pick a file it can't already see.)
- Since the hot loader still uses NETLOAD on itself, it hits the exact same one-time limitation — the `AssemblyVersion` wildcard + `deploy.ps1`'s timestamped filename trick is kept **only** for `AcadHotLoader`, since it is meant to change rarely. The Plugin project needs neither trick: it's never loaded via `LoadFrom`, so there's no identity to collide with and a fixed assembly name/file across builds is correct. Overwriting one fixed file also keeps the deploy folder clean — `HOTLOAD`'s file picker opens in that folder, and timestamped plugin copies would pile up there for you to pick the newest out of on every single reload.
- **Caveat:** AutoCAD's own `[CommandMethod]` scanner only runs for NETLOAD'ed assemblies, so commands in `Wang_MCP_AutoCAD.dll` are *not* auto-registered when it's hot-loaded. `HotLoader.cs` provides `HOTRUN` as a reflection-based stand-in for the dev loop (lists `[CommandMethod]`s found in the currently loaded plugin assembly and runs the chosen one). In production (direct NETLOAD) commands register normally and `HOTRUN` isn't needed.
- **`AcadHotLoader` is plugin-agnostic and must stay that way.** No MCP in its names, commands, messages or code, and no reference to `Wang_MCP_AutoCAD`: it loads whatever plugin DLL is picked. Its commands are `HOTLOAD` / `HOTUNLOAD` / `HOTRUN`, not `MCP*`, so they can never be confused with the plugin's own `MCPSTART` / `MCPSTOP` / `MCPSTATUS` / `MCPLOG`. (The repo-level build plumbing — `WangMcpDeployDir`, `WANG_MCP_DEPLOY_DIR`, `deploy.ps1` — belongs to this repo, not to the hot loader, and is fine.)
- `deploy.ps1 -Project <HotLoader|Plugin>` picks the right behavior for each.

## Architecture requirements for future code

- **Logging goes to a file via `Log` (`Wang_MCP_AutoCAD/Log.cs`), not `Editor.WriteMessage`.** `Editor` access is only legal on the document thread; the file log is writable from anywhere. `Log.MinimumLevel` defaults to `Info` and is changeable at runtime with the `MCPLOG` command, which also prints the log path (`%LOCALAPPDATA%\Wang_MCP_AutoCAD\mcp.log`). Keep `WriteMessage` for things a user typing at the command line should see.
- The MCP/HTTP listener itself runs inside the AutoCAD process (loaded via `NETLOAD`), not as a separate host process. **Decided: `System.Net.HttpListener` + a hand-rolled minimal JSON-RPC layer implementing just `tools/list`/`tools/call`** — not the official MCP C# SDK, which assumes it owns the process lifecycle (top-level host/DI container) that AutoCAD already owns. Both `HttpListener` and `System.Text.Json` are in-box in `net8.0`, which keeps the plugin's package count at zero (see the dependency policy below).
- Since this will not be reachable beyond localhost, bind prefixes to `http://127.0.0.1:<port>/` only — never `+` or `*`, which are the only forms that require elevation or a `netsh http add urlacl` reservation (verified by hand: a non-elevated process binds `localhost` and `127.0.0.1` prefixes fine, and AutoCAD normally runs non-elevated). No transport auth is needed, but still reject requests whose `Origin` header is present and not loopback — that is the MCP spec's DNS-rebinding guard, and it costs one `if`.
- Two AutoCAD sessions cannot share a port. Decide the port allocation and discovery story before writing the listener (scan upward from a base port, then publish the chosen port and the PID somewhere the MCP client can find, e.g. under `%LOCALAPPDATA%\Wang_MCP_AutoCAD\`).

### The listener's lifecycle lives entirely inside `MCPSTART` — there is no background thread

**Decided (verified by hand in a live AutoCAD 2026 session, not just inferred from docs):** `McpExtension.StartCommand` (`MCPSTART`) is declared `async void` and directly `await`s `McpHttpServer.RunAcceptLoopAsync` — the accept loop that calls `HttpListener.GetContextAsync()` in a loop and handles each request inline. The command stays "in progress" for as long as the server is running; it returns only when `MCPSTOP` or `Terminate()` calls `server.Stop()` (closing the `HttpListener`, which unblocks the pending `GetContextAsync()`), or when something inside the loop throws and propagates all the way out — in which case AutoCAD reports `MCPSTART` as a failed command, which is the intended way to surface a bug here rather than logging-and-swallowing it on a thread nobody is watching.

This replaces an earlier design that ran the accept loop on a `Task.Run`-spawned pool thread and marshalled every document-touching tool call onto AutoCAD's document thread via `Application.DocumentManager.ExecuteInApplicationContext`, tracked with a pending-call queue, a `TaskCompletionSource`, a bounded timeout, and an `AbortPending()` path for shutdown. That machinery existed only to keep a request from hanging forever if AutoCAD's UI thread was busy. Once the accept loop itself runs directly on the document thread (because `MCPSTART` awaits it there), none of that is needed: `IDocumentGateway.TryRun` just calls `Document.LockDocument()` inline, which naturally blocks the in-flight request until AutoCAD is free — not a bug to guard against with a timeout, the correct behavior for "don't race a busy UI thread."

**Why this doesn't hang AutoCAD's command line for the server's whole uptime**, despite `[CommandMethod]`s ordinarily being expected to return quickly: AutoCAD's own Windows message loop keeps pumping while an `async` command is suspended at an `await` — it does not block the way a long synchronous call would. This was verified directly (NETLOAD/HOTLOAD into a running session, `MCPSTART`, then drawing entities and using the Properties panel and viewport while the server logged live requests) rather than assumed from documentation alone, since community sources on async `[CommandMethod]` behavior warn about other pitfalls (a command "in progress" can block *other commands* from running, and a continuation resuming on the right thread after an `await` still does not by itself grant document access — `LockDocument()`/a fresh `Transaction` is still required at the point of use, which `AcadDocumentGateway.TryRun` does). If a future AutoCAD version's command executor changes this behavior, the documented fallback is to go back to a plain background `Thread` (not `Task.Run` — see the community-documented pattern for long-lived AutoCAD background work) owning the accept loop, with `MCPSTART` returning immediately as it used to; the inline-locking simplification in `IDocumentGateway` is independent of that choice and should be kept either way.

- **`async void` on `StartCommand` is deliberate, not an oversight.** AutoCAD's command executor invokes `[CommandMethod]`s without awaiting a returned `Task`, so a command returning `Task` would be treated as already finished the moment it hit its first `await` — `async void` is the only shape that keeps the command "in progress" for the loop's whole lifetime.
- **`MCPSTOP` and `Terminate()` do not join the loop.** They call `server.Stop()` and return immediately; there is nothing to wait for because nothing downstream is blocked on their thread — `StartCommand`'s own `await` unwinds on its own once `Stop()` closes the listener, and its `finally` clears the static `_server` field itself. This also means a live listener no longer needs a separate deterministic-join story to avoid pinning the collectible `AssemblyLoadContext` on `HOTLOAD` — once `StartCommand`'s await returns (or throws), the command method itself is finished and releases whatever it was holding.

## Dependency policy: the plugin project takes no NuGet packages

`Wang_MCP_AutoCAD.csproj` has zero `PackageReference`s and should stay that way. This isn't minimalism for its own sake — both deployment modes are actively hostile to third-party dependencies:

- `deploy.ps1` copies only `Wang_MCP_AutoCAD.dll` and its `.pdb` to the deploy folder, so a dependency DLL never arrives at all.
- `PluginLoadContext.Load` returns `null` for any assembly not already in the default context, and `HotLoader.cs` loads the plugin from a `MemoryStream` — there is no path for the runtime to probe alongside. A dependency fails to resolve in the dev loop even if deploy.ps1 copied it.
- That same `Load` matches default-context assemblies **by simple name only, ignoring version**. Any package AutoCAD also happens to load (`Newtonsoft.Json` being the classic) silently binds to Autodesk's version instead of the one that was compiled against.

If a package ever genuinely earns its place, all three have to be fixed first: copy the dependency set in `deploy.ps1`, give `PluginLoadContext` a directory fallback that reads sibling DLLs as *bytes* (loading by path file-locks them against the next rebuild), and make its name match version-aware. Test-only packages in `Wang_MCP_AutoCAD.Tests` are unaffected by all of this and are fine.
