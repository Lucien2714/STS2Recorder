# STS2MCP submodule

`STS2MCP/` is a git submodule pointing at
[STS2MCP](https://github.com/Lucien2714/STS2MCP), pinned to one commit. Its
source files are compiled straight into `STS2_Recorder.dll` — nothing here is
copied into this repo.

## Why compile another repo's source

The recorder's core promise is that a recorded `state` object is *identical* to
what STS2MCP's `GET /api/v1/singleplayer` returns — same fields, same shapes,
same edge-case handling. The only way to guarantee that without ongoing effort
is to run the same code, so `BuildGameState()` is compiled from STS2MCP's own
source instead of being reimplemented or approximated here.

A mod DLL cannot reference another mod's DLL at load time, so the reuse has to
happen at compile time. The submodule gives that plus an exact commit pin, and
the pin lives in git rather than in a hand-maintained manifest.

## What is compiled, and what is not

`STS2_Recorder.csproj` compiles every `*.cs` at the submodule root except two:

| Excluded | Why |
|----------|-----|
| `McpMod.cs` | `HttpListener` + STS2MCP's own `[ModInitializer]`. The recorder must not open a port, and two `[ModInitializer]` classes in one DLL would both run. `src/McpModShim.cs` re-supplies the three members the rest of the class needs from it. |
| `McpMod.SettingsUI.cs` | Harmony patches for STS2MCP's settings panel; the recorder must not alter game settings. |

Everything else is in because `BuildGameState()` is one method on a partial
class whose pieces call freely across each other, so the compilable unit is
nearly the whole class rather than one file. `McpMod.Actions.cs` compiles mostly
as dead code — the recorder is passive and never invokes `ExecuteAction` — but
`BuildGameState()` calls into it, and it doubles as the reference for the action
names and argument shapes the capture layer reproduces.

## Do not edit inside the submodule

A local edit silently breaks the identical-output guarantee — the recorder keeps
working, and its output quietly stops matching STS2MCP's. The build warns when
the submodule working tree has modified sources and stamps recordings
`<sha>-dirty`.

**Fix bugs upstream in STS2MCP, push, then move the pin.**

## First checkout

```powershell
git clone --recurse-submodules <this repo>
# or, in an existing clone:
git submodule update --init --recursive
```

The build fails with a pointer to that command if the submodule is empty.

## Moving the pin

```powershell
git submodule update --remote vendor/STS2MCP   # fast-forward to the tracked branch
git -C vendor/STS2MCP checkout <sha>           # or pin an exact commit
git add vendor/STS2MCP
git commit -m "chore: bump STS2MCP to <sha>"
```

The tracked branch is recorded in `.gitmodules`. A bump can change the state
schema, so give it its own commit — that commit is the boundary a dataset
spanning the change has to be split on.

## Provenance

The build reads the submodule's checked-out commit
(`STS2_Recorder.csproj` → `ResolveStateBuilderCommit`) and stamps it onto the
assembly; every recording carries it as `state_builder_commit`. That field is
what makes a dataset collected across a schema change interpretable after the
fact.

The stamp reflects what is actually checked out, not the pin recorded in git —
so a local `git checkout` inside the submodule is reported honestly. Where git
is unavailable the stamp degrades to `unknown` with a build warning rather than
failing the build.
