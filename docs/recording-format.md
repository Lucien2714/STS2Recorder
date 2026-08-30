# Recording format

One file per run, written to `output_dir` as:

```
run_<yyyyMMdd>_<HHmmss>_<runId>.json
```

`runId` is the run's start time in epoch seconds. The game mints it when a run
begins and stores it in the save, passing it back unchanged on load, so it is
identical for a run and every continue of it.

It is not readable while a run is in progress — the game publishes it only in the
save data — so a recording is created under a `pending<timestamp>` id and takes
its real one from the first save, a second or two in. Nothing reaches disk before
that, so a file carries a `pending…` id only if its run ended without ever
saving; `run.start_time` is `0` in exactly that case.

Quitting to the menu and continuing the run later appends to the **same file**:
the recorder looks a run up by its id when the id resolves, so a run played over
several sittings is one recording. The `<yyyyMMdd>_<HHmmss>` in the name is when
the run was *first* recorded and never changes. `resume_count` counts the
continues, and the first step of each sitting after the first carries
`resumed: true`.

A sitting starts a separate file only when the existing one cannot honestly be
continued — it already has an `outcome`, or its steps came from a different
recorder or STS2MCP build, which the log says at the time.

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
| `run_id`               | The run's start time in epoch seconds; see above. Equal to `run.start_time`. |
| `save_count`           | Number of game saves that flushed this file. |
| `resume_count`         | Times the run was continued after being quit to the menu. |
| `outcome`              | Absent while the run is in progress or merely left for the main menu — a save-and-quit is not an ending. Present exactly when the run finished: victory, death, or abandon, including a run abandoned from the main menu without being loaded (its file is reopened and stamped). |

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

Every field here is mirrored off the game's save data, so it is absent until the
run's first save — the same moment `run_id` resolves. Nothing in the live run
exposes these: the game builds its run-history record only when a run *ends*, and
keeps it on a process-wide singleton, so reading it during a run yields either
nothing or the previous run's character and seed.

`num_reloads` is the game's own counter. A reloaded run can replay the same
decision point twice; consumers training on this data usually want to know that
happened.

## `steps` — TrajectoryStep[]

```jsonc
{
  "index": 0,
  "t": "2026-08-29T20:45:31Z",
  "state": { "state_type": "combat", /* ... */ },
  "action": {
    "action": "play_card",
    "args": { "card_index": 2, "target": "kin_priest_0" },
    "label": "Strike",
    "subject": {
      "kind": "card",
      "id": "STRIKE",
      "name": "Strike",
      "is_upgraded": true,
      "upgrade_level": 1,
      "enchantment": { "id": "SEARING", "amount": 2 }
    }
  },
  "resumed": true
}
```

| Field     | Notes |
|-----------|-------|
| `state`   | Verbatim `BuildGameState()` output — byte-identical in shape to STS2MCP's `GET /api/v1/singleplayer`. Captured **before** the action took effect, so it is the state the decision was made *from*, not its result. Null if a snapshot failed; the step is still recorded rather than dropped. |
| `action`  | Null **only** on the terminal step, which records the final state with nothing following it. That step is written the moment the run *ends*, while the state is still intact, so it holds the position the run ended in — for a death, the combat board as the player died, with `state_type` still `monster` and `hp: 0`, because the game-over screen has not been pushed yet. Read `outcome`, not `state_type`, to tell that a run is over. A run left for the menu has no terminal step. |
| `resumed` | Present and `true` only on the first step after resuming from a save. Omitted otherwise. |

Branch on `state.state_type` (`combat`, `map`, `event`, `shop`, `card_reward`,
`crystal_sphere`, `game_over`, `menu`, …) exactly as an MCP client would.

### `action`

`action` and `args` use STS2MCP's action API vocabulary, so a recorded step can
be replayed by POSTing `{ "action": ..., ...args }` to
`/api/v1/singleplayer`. `label` is a human-readable description of what was acted
on; it is not needed to replay the step, and is there to make files readable by
eye.

`subject` identifies what was acted on in its own right, rather than by where it
sat in a list. `card_index` is what replaying needs, but it does not say what the
card *was*: two cards can share a name and a hand position and still differ in
what they do. The state's hand entry carries a card's `id` and `is_upgraded`, but
**not** its enchantment — so for `play_card`, `subject` is the only place a
recording says whether the Strike that was played was enchanted, and to what
degree it was upgraded. It is absent for actions whose subject the paired state
already pins down, and never needed to replay a step.

Captured today:

| `action` | `args` |
|----------|--------|
| `play_card` | `card_index`, and `target` when the card was aimed at something |
| `end_turn` | — |
| `use_potion` | `slot`, and `target` when the potion was aimed |
| `discard_potion` | `slot` |
| `choose_map_node` | `index` into the state's `next_options` |
| `claim_treasure_relic` | `index` |
| `choose_event_option` | `index` into the state's `event.options` |
| `advance_dialogue` | — (Ancient rooms) |
| `choose_rest_option` | `index` into the state's `rest_site.options` |
| `claim_reward` | `index` into the state's `rewards.items` |
| `skip_card_reward` | — |
| `select_relic` | `index` |
| `proceed` | — |

Arguments are derived from the state recorded in the same step — a `card_index`
indexes that step's `player.hand`, a `target` names an `entity_id` from its
`battle.enemies`, a map `index` is one of its `map.next_options`. They mean what
they say relative to the state beside them, not to the game at some other moment.

An `index` is omitted when it could not be resolved against the state — the step
still records what was chosen in `label`, rather than carrying an index that
would point at the wrong thing.

Still uncaptured: shop purchases, card reward selection (`select_card_reward`),
the card-selection and bundle screens, in-combat card selection, the Crystal
Sphere, and `skip_relic_selection`.

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
