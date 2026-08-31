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
continued — it already has an `outcome`, or its steps were recorded by a
different recorder build or on a different game version, which the log says at
the time.

The file is **rewritten in full** on every flush, never appended to. It is
therefore always valid JSON: a run cut short by a crash or an alt-F4 still leaves
a readable file containing everything up to the last save.

## Top level

```jsonc
{
  "schema_version": 2,
  "recorder_version": "0.1.0",
  "game_version": "v0.107.1",
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
| `schema_version`       | Bumped on any breaking change to this document. **2** dropped `state_builder_commit` in favour of `game_version`. |
| `game_version`         | The game build every `state` below was read out of, as the game names itself (`v0.107.1`). The state is a picture of the game's own model, so this is what sets what a recorded field *means* — cards get reworked, rooms get added, fields come and go. **Read it, with `recorder_version`, before comparing files collected at different times**: together they are what makes a dataset spanning an update interpretable. `"unknown"` if the game did not say. |
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
      "max_upgrade_level": 2,
      "enchantment": { "id": "SEARING", "name": "Searing", "amount": 2 },
      "floor_added_to_deck": 0
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
card *was*, and neither does the state: STS2MCP records what a card is **printed**
with — id, name, type, cost, rarity, and a bare `is_upgraded` — and nothing about
what the run has since done to it. Two Strikes in the same hand can differ in how
far they are upgraded, what they are enchanted with, what afflicts them, and
whether they are real deck cards or copies conjured for one combat; the state
calls all of them "Strike, upgraded". `subject` is where the difference lives. It
is never needed to replay a step.

Every card-related action carries one — `play_card`, `select_card`,
`select_card_reward`, `combat_select_card`, and `shop_purchase` when a card was
bought — and `select_bundle` carries one per card in the pack.

#### `subject` for a card

| Field | Notes |
|-------|-------|
| `kind` | `"card"`. |
| `id`, `name` | The card's model id, and its name as the state beside it spells the same card. Match on `id`; `name` is localized and for reading by eye. |
| `is_upgraded`, `upgrade_level`, `max_upgrade_level` | A level means nothing without its ceiling: `+1` is a finished card for most, and a third of the way for a card that upgrades three times. |
| `enchantment`, `affliction` | `{ id, name, amount }`, each present only when the card carries one. **The state records neither, anywhere** — this is the only place a recording says a card was enchanted or afflicted. |
| `is_clone`, `is_dupe` | Present only when `true`. A copy conjured for one combat, not a deck card: a play of one is not a play of the card it was copied from, and a deck reconstructed from this file must not count it. |
| `floor_added_to_deck` | The floor the card joined the deck on, when the game knows it. Absent for a card still on offer in a shop or a reward. |

#### `subject` for a bundle

`select_bundle` adds several cards on one click, so its subject is
`{ "kind": "bundle", "card_count": n, "cards": [ … ] }`, each entry a card
subject exactly as above. Without it, a file could not say what the deck gained.

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
| `select_card_reward` | `card_index` into the state's `card_reward.cards` |
| `skip_card_reward` | — |
| `select_relic` | `index` |
| `skip_relic_selection` | — |
| `shop_purchase` | `index` into the state's `shop.items` |
| `combat_select_card` | `card_index` into the state's `hand_select.cards` |
| `combat_confirm_selection` | — |
| `select_card` | `index` into the state's `card_select.cards` |
| `confirm_selection` | — |
| `cancel_selection` | — (also the choose-a-card screen's skip) |
| `select_bundle` | `index` into the state's `bundle_select.bundles` |
| `confirm_bundle_selection` | — |
| `cancel_bundle_selection` | — |
| `proceed` | — |

Arguments are derived from the state recorded in the same step — a `card_index`
indexes that step's `player.hand`, a `target` names an `entity_id` from its
`battle.enemies`, a map `index` is one of its `map.next_options`. They mean what
they say relative to the state beside them, not to the game at some other moment.

An `index` is omitted when it could not be resolved against the state — the step
still records what was chosen in `label`, rather than carrying an index that
would point at the wrong thing.

`select_card` covers both card-selection screens: the "choose a card" offer an
Ancient room's Arcane Scroll opens, where the pick resolves the screen on its
own, and the deck grids (upgrade, transform, enchant, remove, in-combat piles),
where it is a *toggle* that a `confirm_selection` finishes — so a grid selection
of two cards records two `select_card` steps and a `confirm_selection`, and a
card picked and unpicked records twice. Each step's state holds the cards that
were on offer under `card_select.cards`; `select_bundle`'s holds the packs under
`bundle_select.bundles`.

`select_card`'s `subject` earns its place here more than anywhere: choosing which
card to upgrade, transform or remove is a choice *between* cards the state prints
identically, and the subject is what says which one it actually was.

`shop_purchase` covers the merchant's cards, relics, potions and card removal,
and the fake merchant event's relics — its items are the same shop entries, so
its `index` reads against `fake_merchant.shop.items` instead. A purchase the shop
then refuses (sold out, not enough gold, no free potion slot) is **not** recorded:
the step is taken back the moment the game reports the failure, so a `shop_purchase`
in a file is always a sale that went through. Items the game buys *for* the
player — Lord's Parasol clearing the shop on arrival — are not recorded either,
because nobody chose them; they show up as the gold and inventory changes in the
next step's state.

Buying a card removal opens a card grid, so it records as a `shop_purchase`
followed by the `select_card` and `confirm_selection` that pick the card to
remove. Backing out of that grid leaves the `shop_purchase` in place followed by
a `cancel_selection` — which is what the player did.

`combat_select_card` is a card handed over mid-combat because a card asked for
one — discard, exhaust, upgrade — not a card played; playing one is `play_card`,
which comes from the action queue. Picking a card moves it out of the hand, so
each pick's `card_index` indexes that step's own `hand_select.cards`, and a
multi-card selection ends with `combat_confirm_selection`.

Still uncaptured: the Crystal Sphere.

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
