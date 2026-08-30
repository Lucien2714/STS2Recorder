# STS2 Recorder

A [Slay the Spire 2](https://store.steampowered.com/app/2868840/) mod that
records a human playthrough as a **(state, action) trajectory** and writes it to
a JSON file, flushed every time the game saves.

One file per run:

```
run_20260829_204512_1756500312.json
    ^date    ^time  ^run start time (epoch seconds)
```

Each entry pairs the full game state the player was looking at with the action
they took from it — the shape you want for imitation learning, run analysis, or
replaying a human run through an agent harness.

## Relationship to STS2MCP

State serialization is not reimplemented here. The recorder compiles
[STS2MCP](https://github.com/Lucien2714/STS2MCP)'s `BuildGameState()` verbatim
from vendored source, so a recorded `state` object is **identical** to what
STS2MCP's `GET /api/v1/singleplayer` returns, and captured actions use the same
names and argument shapes as its action API.

Practically, that means a recorded human run and an agent's run through the MCP
server are the same data format, and every recording is stamped with the exact
upstream commit that produced it. See [`vendor/README.md`](vendor/README.md).

The two mods are independent and can be installed side by side; the recorder
opens no ports and never enqueues an action.

## Status

Early. What works today:

- [x] Loads alongside STS2MCP, opens no port, alters no gameplay
- [x] Detects run start, resume, and end; one file per run across a continue
- [x] Flushes on every game save
- [x] Writes run metadata and outcome
- [ ] Combat action capture (`play_card`, `end_turn`)
- [ ] Navigation and UI action capture (map, events, rest, shop, rewards)

Until the capture layer lands, files contain run metadata and a terminal state
but no intermediate steps.

## Requirements

- Slay the Spire 2 installed
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- A checkout of [STS2MCP](https://github.com/Lucien2714/STS2MCP) (to vendor from
  and to drift-check against)
- PowerShell 7+ for the build and vendor scripts

## Build

```powershell
# One-off: point the build at your game install
Copy-Item Directory.Build.props.example Directory.Build.props   # then edit

# Populate the vendored STS2MCP sources
.\scripts\sync-upstream.ps1 -Source E:\path\to\STS2MCP

# Build, and optionally install into <game>\mods\
.\build.ps1
.\build.ps1 -Install
```

A clean build must produce **zero warnings and zero errors**.

Building requires a real game install — the project references `sts2.dll`,
`GodotSharp.dll` and `0Harmony.dll` from it, so there is no way to build in a
bare checkout.

## Install

Copy into `<game_install>/mods/`:

- `out/STS2_Recorder/STS2_Recorder.dll`
- `mod_manifest.json`, renamed to `STS2_Recorder.json`

Or use `.\build.ps1 -Install`.

## Configuration

On first launch the mod writes `STS2_Recorder.conf` next to its DLL:

```json
{
  "enabled": true,
  "output_dir": "C:/Users/<you>/AppData/Roaming/Godot/app_userdata/sts2/.../recordings",
  "pretty_print": true
}
```

| Key            | Default                      | Meaning |
|----------------|------------------------------|---------|
| `enabled`      | `true`                       | Master switch; `false` loads the mod but records nothing. |
| `output_dir`   | `<game user data>/recordings` | Where trajectory files go. |
| `pretty_print` | `true`                       | Indent the JSON. Roughly 2–3× the file size; turn off for bulk collection. |

Changes take effect on restart.

## Scope and limitations

- **Singleplayer only.** In co-op, player intent is routed through many separate
  network synchronizers rather than the local UI path the capture layer hooks,
  so a co-op recording would be partial and misleading. Co-op runs are detected
  and skipped rather than half-recorded.
- **No unit tests.** Effectively all of this code touches live game state;
  verification is a manual in-game smoke test, matching STS2MCP's approach.
- **Passive by design.** The mod reads state and observes input. It never
  enqueues an action, mutates game state, or changes settings. A bug in it
  should cost recording fidelity, never someone's run.

## Format

See [`docs/recording-format.md`](docs/recording-format.md).

## License

MIT
