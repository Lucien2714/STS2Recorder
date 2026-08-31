# STS2MCP, vendored

`STS2MCP/` is a **copy** of source files from
[STS2MCP](https://github.com/Gennadiyev/STS2MCP) by Yikun Ji (Kunologist), MIT
licensed — see `STS2MCP/LICENSE`, which travels with the code and must stay
there. They are compiled straight into `STS2_Recorder.dll`.

It used to be a git submodule. It is a copy now so the code can be **fixed
here**, without a round trip through the upstream repo for every change the
recorder needs.

## Why compile another project's source

The recorder's core promise is that a recorded `state` object is *identical* to
what STS2MCP's `GET /api/v1/singleplayer` returns — same fields, same shapes,
same edge-case handling. The only way to guarantee that without ongoing effort
is to run the same code, so `BuildGameState()` is compiled from STS2MCP's own
source instead of being reimplemented or approximated here.

A mod DLL cannot reference another mod's DLL at load time, so the reuse has to
happen at compile time.

## What was copied, and why each file is here

`STS2_Recorder.csproj` names these three files one at a time — the copy holds
only what the serializer needs to compile, and nothing arrives here by wildcard:

| File | Why it is here |
|------|----------------|
| `McpMod.StateBuilder.cs` | `BuildGameState()` itself. This is the file the recorder exists to share. |
| `McpMod.Helpers.cs` | The scene-tree and text helpers it is built on. The capture layer calls these too, so an index it records means the same thing as an index in the state beside it. |
| `McpMod.StateBuilderSupport.cs` | **Not an upstream file.** Three members of `McpMod.Actions.cs` that `BuildGameState()` calls, lifted out verbatim. See below. |

Deliberately **not** copied:

| Left behind | Why |
|-------------|-----|
| `McpMod.cs` | `HttpListener` + STS2MCP's own `[ModInitializer]`. The recorder must not open a port, and two `[ModInitializer]` classes in one DLL would both run. `src/McpModShim.cs` re-supplies the two members the rest of the class needs from it. |
| `McpMod.Actions.cs` | The action executors. The recorder is passive and never invokes `ExecuteAction`, so all ~88 KB of it is code that cannot run here — and it drags in the two files below. Its three members that `BuildGameState()` does call live in `McpMod.StateBuilderSupport.cs` instead. Upstream is still the place to read for the action names and argument shapes the capture layer reproduces. |
| `McpMod.Profile.cs`, `McpMod.Compendium.cs` | Only ever reached from `McpMod.Actions.cs`. |
| `McpMod.SettingsUI.cs` | Harmony patches for STS2MCP's settings panel; the recorder must not alter game settings. |
| `McpMod.Formatting.cs` | Markdown rendering for API responses. Nothing here has responses. |
| `McpMod.Wiki.cs` | The wiki endpoint. |
| `McpMod.MultiplayerState.cs`, `McpMod.MultiplayerActions.cs` | Co-op. The recorder is singleplayer-only and skips a co-op run outright. |

If a compile fails on a name that does not exist, there are two honest fixes:
lift the missing member into `McpMod.StateBuilderSupport.cs` the way the three
in there got lifted, or — if it pulls in too much to lift — copy the whole file
that defines it and add it to the list in the csproj. Prefer the first; a file
copied to satisfy one symbol brings its own dependencies with it, which is how
88 KB of unreachable executors got here in the first place.

## Editing this code

Editing is now allowed — that is the point of the copy — but it is not free:

- **An edit to `BuildGameState()` breaks the identical-output guarantee.** The
  recorder keeps working and its output quietly stops matching STS2MCP's, and
  nothing in a recording says so on its own. Anything that alters the shape of
  the emitted state needs a note in `docs/recording-format.md` and, if it breaks
  consumers, a `schema_version` bump.
- **Bump the version in `mod_manifest.json`.** These files are under the same
  rule as `src/` now: a run continued across a build that records differently
  has to start a new file, and `recorder_version` is what
  `RecordingSession.TryContinueExistingRecording` compares to notice. The old
  submodule layout caught an edit here on its own by stamping `-dirty`; a copy
  in-tree has no upstream to differ from, so this is a habit rather than a
  check.
- **Fixes worth having are worth sending upstream.** A bug fixed only here is a
  bug the next re-sync can quietly undo.
- **Keep edits marked.** A comment saying what was changed and why is what makes
  the next re-sync possible.
- **`McpMod.StateBuilderSupport.cs` is the exception that proves the rule.** It
  is ours, but its bodies are upstream's, copied unchanged so they cannot drift.
  Re-copy them on a re-sync rather than editing them here.

## Provenance, and re-syncing

`STS2_Recorder.csproj` records where the copy came from:

```xml
<Sts2McpUpstreamCommit>2b89ea2ffd65bcf0202aaa8b632a62113a359add</Sts2McpUpstreamCommit>
<Sts2McpUpstreamRef>Lucien2714/STS2MCP@feat/custom-run</Sts2McpUpstreamRef>
```

These are documentation, read by people rather than by the build. Recordings do
not carry a serializer stamp: they carry `game_version` — the game build the
state was read out of, which is what actually decides what a recorded field
means — and `recorder_version`, which pins this repo's code, these files
included. A recording made before and after an edit here is told apart by the
mod version, so bump it.

To re-sync a file from upstream, overwrite it, re-apply whatever local edits it
carried, update both properties above, and give it its own commit. A re-sync can
change the state schema, and that commit is the boundary a dataset spanning the
change has to be split on.
