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

State serialization is not reimplemented here. Source files from
[STS2MCP](https://github.com/Gennadiyev/STS2MCP) are vendored at
`vendor/STS2MCP`, and its `BuildGameState()` is compiled straight into this mod,
so a recorded `state` object is **identical** to what STS2MCP's
`GET /api/v1/singleplayer` returns, and captured actions use the same names and
argument shapes as its action API.

Practically, that means a recorded human run and an agent's run through the MCP
server are the same data format. Every recording carries the game version it was
read out of, so a dataset spanning a game update stays interpretable. See
[`vendor/README.md`](vendor/README.md).

The two mods are independent and can be installed side by side; the recorder
opens no ports and never enqueues an action.

## Status

Early. What works today:

- [x] Loads alongside STS2MCP, opens no port, alters no gameplay
- [x] Detects run start, resume, and end; one file per run, keyed on the game's
      own run id, across any number of quit-and-continue sittings
- [x] Records the outcome of victory, death, and abandon — including abandoning
      from the main menu without loading the run
- [x] Flushes on every game save
- [x] Writes run metadata and outcome
- [x] Combat action capture (`play_card`, `end_turn`, potions)
- [x] Route capture (`choose_map_node`, `claim_treasure_relic`, `proceed`)
- [x] Room capture (events including Ancient rooms, rest sites, rewards, relic
      choices)
- [x] Card-selection screens (`select_card`, `select_bundle`, and the confirm,
      cancel and skip that close them) — the cards on offer are recorded with
      the pick
- [x] Shop purchases (`shop_purchase`), merchant and fake merchant alike
- [x] Card rewards (`select_card_reward`), relic skips, and in-combat hand
      selection (`combat_select_card`)
- [ ] Crystal Sphere

## Requirements

- Slay the Spire 2 installed
- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- PowerShell 7+ for the build script

A plain `git clone` is enough — the STS2MCP sources are vendored in the repo.

## Build

```powershell
# One-off: point the build at your game install
Copy-Item Directory.Build.props.example Directory.Build.props   # then edit

# Build, and optionally install into <game>\mods\
.\build.ps1
.\build.ps1 -Install
```

A clean build must produce **zero warnings and zero errors**.

Building requires a real game install — the project references `sts2.dll`,
`GodotSharp.dll` and `0Harmony.dll` from it, so there is no way to build in a
bare checkout.

## Install

The game loads a mod from its **own folder** under `mods/`, so copy both files
into `<game_install>/mods/STS2Recorder/`:

- `out/STS2_Recorder/STS2_Recorder.dll`
- `mod_manifest.json` (keep the name)

Or use `.\build.ps1 -Install`, which puts them there and prints the installed
DLL's timestamp. Close the game first — it holds the DLL open while running, and
an install that cannot replace it is a fix that silently never loads. The mod
logs its build time at startup, so the Godot console confirms which DLL is
actually running.

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

## Credits

This mod stands on **[STS2MCP](https://github.com/Gennadiyev/STS2MCP)** by
**Yikun Ji ([Kunologist](https://github.com/Gennadiyev))**, MIT licensed.

Its game-state serializer is not merely a dependency here — it *is* the state
format this recorder writes. Its source is vendored under
[`vendor/STS2MCP`](vendor/STS2MCP) and compiled into `STS2_Recorder.dll`, with
their license alongside it; the capture layer additionally follows STS2MCP's
action API for the name and arguments of every recorded decision. Without that
work, matching an agent's view of the game to a human's would have meant
reimplementing — and forever chasing — a serializer someone had already written
well.

The vendored copy carries local changes. It does not speak for upstream, and any
bug you find in a recording is this repo's to answer for. See
[`vendor/README.md`](vendor/README.md).

## License

MIT for this mod — see [`LICENSE`](LICENSE).

The vendored STS2MCP sources under `vendor/STS2MCP` are MIT licensed and
copyright Yikun Ji; their license travels with them in
[`vendor/STS2MCP/LICENSE`](vendor/STS2MCP/LICENSE).
