# Recording format

One file per run, written to `output_dir` as:

```
run_<yyyyMMdd>_<HHmmss>_<runId>.json
```

`runId` is the run's start time in epoch seconds, taken from the game's own run
history. It is persisted in the save, so resuming a run reuses the same filename
rather than starting a second file.

The file is **rewritten in full** on every flush, never appended to. It is
therefore always valid JSON: a run cut short by a crash or an alt-F4 still leaves
a readable file containing everything up to the last save.

## Top level

```jsonc
{
  "schema_version": 1,
  "recorder_version": "0.1.0",
  "state_builder_commit": "2b89ea2ffd65bcf0202aaa8b632a62113a359add",
  "run_id": "1756500312",
  "run": { /* RunMeta */ },
  "started_at": "2026-08-29T20:45:12Z",
  "last_saved_at": "2026-08-29T21:13:04Z",
  "save_count": 14,
  "resume_count": 0,
  "steps": [ /* TrajectoryStep[] */ ],
  "outcome": { /* RunOutcome, null until the run ends */ }
}
```

| Field                  | Notes |
|------------------------|-------|
| `schema_version`       | Bumped on any breaking change to this document. |
| `state_builder_commit` | The STS2MCP commit whose `BuildGameState()` produced every `state` below. **Read this before comparing files collected at different times** — it is what makes a dataset spanning a schema change interpretable. |
| `save_count`           | Number of game saves that flushed this file. |
| `resume_count`         | Times the run was resumed from a save while being recorded. |
| `outcome`              | Absent while the run is in progress. |

## `run` — RunMeta

Facts fixed for the life of the run.

```jsonc
{
  "character": "Ironclad",
  "seed": "ABC123",
  "ascension": 4,
  "game_mode": "Standard",
  "num_reloads": 0,
  "start_time": 1756500312
}
```

`num_reloads` is the game's own counter, mirrored on each save. A reloaded run
can replay the same decision point twice; consumers training on this data usually
want to know that happened.

## `steps` — TrajectoryStep[]

```jsonc
{
  "index": 0,
  "t": "2026-08-29T20:45:31Z",
  "state": { "state_type": "combat", /* ... */ },
  "action": {
    "action": "play_card",
    "args": { "card_index": 2, "target": "KIN_PRIEST_0" },
    "label": "Strike"
  },
  "resumed": true
}
```

| Field     | Notes |
|-----------|-------|
| `state`   | Verbatim `BuildGameState()` output — byte-identical in shape to STS2MCP's `GET /api/v1/singleplayer`. Captured **before** the action took effect, so it is the state the decision was made *from*, not its result. Null if a snapshot failed; the step is still recorded rather than dropped. |
| `action`  | Null **only** on the terminal step, which records the final state with nothing following it. |
| `resumed` | Present and `true` only on the first step after resuming from a save. Omitted otherwise. |

Branch on `state.state_type` (`combat`, `map`, `event`, `shop`, `card_reward`,
`crystal_sphere`, `game_over`, `menu`, …) exactly as an MCP client would.

### `action`

`action` and `args` use STS2MCP's action API vocabulary, so a recorded step can
be replayed by POSTing `{ "action": ..., ...args }` to
`/api/v1/singleplayer`. `label` is a human-readable description of what was acted
on; it is not needed to replay the step, and is there to make files readable by
eye.

Note that card indices shift as cards leave the hand — the same right-to-left
ordering caveat that applies to the MCP API applies to replaying these steps.

## `outcome` — RunOutcome

```jsonc
{
  "victory": false,
  "abandoned": false,
  "floor_reached": 23,
  "ended_at": "2026-08-29T21:40:11Z"
}
```

## Conventions

- All timestamps are UTC ISO-8601, second precision.
- Keys are `snake_case`.
- Null-valued fields are omitted entirely rather than written as `null`, matching
  STS2MCP's serializer. An absent key means "no value", not "unknown".

## Schema history

| Version | Change |
|---------|--------|
| 1       | Initial format. |
