# Game Definition Schema — cards-game/v1

Games are defined as JSON files loaded at startup. The engine reads them to drive a state machine. Complex logic is handled by named **logic modules** (C# classes registered in the engine). Simple games may need no custom C# at all.

---

## Top-Level Structure

```json
{
  "id": "hearts",
  "name": "Hearts",
  "version": "1.0",
  "deck": "standard-52",
  "players": { "min": 4, "max": 4 },
  "teams": false,
  "rounds": { "repeat_until": "win_condition", "dealer": "rotates_left", "first_dealer": "random" },
  "zones": [...],
  "deal": {...},
  "phases": [...],
  "scoring": {...},
  "win_condition": {...},
  "house_rules": [...],
  "help": "hearts.md",
  "ui": { "card_scale": 1.0, "auto_sort_hand": "none", "allow_sort": true, "show_game_log": true }
}
```

### `extends`
Inherit another game's definition and override specific fields. Useful for variants.
```json
"extends": "texas-holdem",
"overrides": {
  "scoring.wilds": [{ "rank": "2" }],
  "players": { "min": 2, "max": 6 },
  "win_condition.score": 20
}
```

Merge rules: the parent is deep-cloned, then direct child fields (`id`, `name`, `version`, `help`, `house_rules`, `ui`, `tags`) overwrite the parent's, and then each `overrides` entry is applied as a path patch (same syntax as `house_rules[].affects`). `extends` and `overrides` are stripped from the merged result.

---

## Deck

| Value | Description |
|---|---|
| `"standard-52"` | Standard 52-card deck |
| `"standard-52-jokers"` | Standard 52 + 2 jokers |
| `"euchre-24"` | 9–A of all 4 suits (24 cards) |
| `"pinochle-48"` | 9–A of all 4 suits × 2 (48 cards) |
| `"standard-104"` | Two standard decks shuffled together |

The names above are shorthand. A deck can instead state its composition, which is what
lets a game use any deck rather than one someone has already named:

```json
"deck": {
  "ranks":  "2-A",                  // a range, a list ["9","10","J"], or "standard" / "short"
  "suits":  ["hearts", "spades"],   // optional; all four by default
  "copies": "players + 1",          // a number, an expression, or max_players tiers
  "jokers": "(players + 1) * 2"
}
```

`ranks` — `"2-A"`, `"9-A"`, `"10-A"`; or an explicit list; or `"standard"` (2–A) /
`"short"` (9–A). A range must run low to high.

`suits` — restricting the four is supported. Inventing a fifth is not: suits are drawn as
hand-authored vector paths, so a new one needs artwork before it needs parsing.

`copies` and `jokers` — a number, an [expression](#expressions), or tiers selected by
table size:

```json
"copies": [ { "max_players": 4, "count": 5 }, { "count": 6 } ]
```

An unknown deck name, or a declaration producing no cards, **fails the definition** — it
does not fall back to a standard 52. A deck silently coming out the wrong size is a bug
that surfaces much later and somewhere unrelated, and across two clients it deals
different cards from the same definition with no error on either side.

---

## Players

```json
"players": { "min": 2, "max": 6 }
"players": { "min": 4, "max": 4, "names": ["You", "West", "North", "East"] }
```

`names`: display names indexed by seat (index 0 = human player). Defaults to "Player 1", "Player 2", … when absent.

`starting_score`: initial chip/score count per player. Used by poker variants with `win_condition: last_with_chips`. Default `0`.

---

## Teams

```json
"teams": false
```
```json
"teams": {
  "count": 2,
  "size": 2,
  "arrangement": "alternating",
  "only_when_players": [4]
}
```

`arrangement` values: `"alternating"` (0,2 vs 1,3), `"sequential"` (0,1 vs 2,3)

`only_when_players`: when set, teams are only active for games with these exact player counts; otherwise the game plays as individual.

---

## Rounds

```json
"rounds": {
  "repeat_until": "win_condition",
  "dealer": "rotates_left",
  "first_dealer": "random"
}
```

`repeat_until`: `"win_condition"` (default) | `"fixed:4"` (fixed number — overrides win_condition.count)

`dealer`: `"rotates_left"` (default) | `"rotates_right"` | `"winner"` | `"loser"` | `"alternates"`

`first_dealer`: `"random"` (default) | `"high_card"` (not yet implemented — falls back to random)

### How multi-round games work

Route the final phase's `next` to `"new_round"`:
```json
{ "id": "score", "type": "score", "next": "new_round" }
```

The engine automatically:
1. Checks the win condition; if met, transitions to `game_over`.
2. Increments `state.RoundNumber`, rotates `state.DealerId`.
3. Clears hand/spread/trick/pile/deck zones (score zones persist).
4. Re-deals using the same `deal` block.
5. Resets to the first phase.

`state.DealerId` is set from `rounds.first_dealer` at game start and updated each round.

---

## Zones

Zones are named areas where cards reside.

```json
"zones": [
  { "id": "deck",         "type": "deck",   "visibility": "none" },
  { "id": "discard",      "type": "pile",   "visibility": "top" },
  { "id": "hand",         "type": "hand",   "owner": "each_player", "visibility": "owner" },
  { "id": "table",        "type": "spread", "owner": "each_player", "visibility": "all" },
  { "id": "shared_table", "type": "spread", "visibility": "all" },
  { "id": "trick",        "type": "trick",  "visibility": "all" },
  { "id": "community",    "type": "spread", "visibility": "all" },
  { "id": "meld",         "type": "spread", "owner": "each_player", "visibility": "all" },
  { "id": "won_tricks",   "type": "pile",   "owner": "each_player", "visibility": "count_only" },
  { "id": "grid",         "type": "grid",   "owner": "each_player", "rows": 2, "cols": 3, "initial_face": "down", "peek_count": 2 },
  { "id": "pot",          "type": "pot",    "visibility": "all" }
]
```

### Zone Types

| Type | Description |
|---|---|
| `deck` | Shuffled draw pile, hidden |
| `pile` | Ordered stack, configurable visibility |
| `hand` | Player's held cards |
| `spread` | Face-up fan of cards (melds, community, table) |
| `trick` | Cards played to the current trick; cleared after each trick |
| `grid` | Fixed N×M grid of cards (Golf). `rows`/`cols` define layout. `peek_count`: after dealing, flip this many cards face-up per player (Golf's "peek at 2"). |
| `pot` | Virtual zone for chips/point tracking |

### `arrangement`

How a zone's cards sit on the table — geometry only; what may be *seen* stays
`visibility`'s job:

| Value | Layout |
|---|---|
| `"full"` | Side by side, every card fully visible |
| `"compact"` | Overlapped so each card's index and suit still show |
| `"stack"` | Top card only, with a count badge |

Omitted, a zone takes the natural default for its shape: hands fan, decks and piles
stack, spreads lay out full, and grouped spreads (melds) lay out compact. On a grouped
zone the arrangement applies to **each meld**, not to the zone as one row.

The declaration is a preference, not a promise: a `full` run that will not fit the
table overlaps instead, and a compact run that still will not fit shrinks — one
fifteen-card meld must not break the layout for everyone. Any other value fails the
definition.

### Visibility Values

All values are enforced by `GameStateMask` when creating agent snapshots.

| Value | Description |
|---|---|
| `"none"` | No cards visible to anyone |
| `"top"` | Only the top card is visible to everyone |
| `"owner"` | Full card list visible only to the zone's owner |
| `"all"` | Visible to everyone |
| `"count_only"` | Card count exposed via metadata; no cards visible |
| `"top_to_dealer"` | Top card visible only to the dealer (`state.DealerId`); Euchre kitty |

---

## Deal

Simple deal at game/round start:
```json
"deal": {
  "cards_per_player": 13,
  "remainder_to": "deck",
  "face": "down",
  "then_flip_top_to": "discard",
  "anim_delay_ms": 130
}
```

All fields are optional. `face` values: `"up"` | `"down"` (default) | `"owner"` (face-up only in zones visible to all).

For patterned deals (e.g., Euchre), use `"pattern": "3-2"` instead of `cards_per_player`.  Each number is the batch size dealt to all players in one clockwise pass — `"3-2"` gives 5 cards per player dealt in two passes.

For multi-step deals with per-player explicit steps use `anim_deal_steps` (internal). For games requiring mixed face-up/face-down in a non-uniform sequence (e.g., Blackjack), the logic class performs the deal itself and calls `StandardDealEngine.RecordResult`.

For multi-phase deals (Stud, Poker community cards), use `deal` phases instead (see Phases).

---

## Phases

Phases are the steps of a round. Each has an `id`, `type`, configuration, and a `next` pointer.

```json
{
  "id": "play",
  "type": "trick_taking",
  "next": "score"
}
```

Conditional `next`:
```json
"next": {
  "if": "win_condition",
  "then": "end",
  "else": "deal_new_round"
}
```

---

## Phase Types

All phase types are registered in `PhaseHandlerRegistry`.

| Type | Status | Games |
|---|---|---|
| `trick_taking` | Implemented | Hearts, Spades, Euchre, Pinochle |
| `bidding` | Implemented | Spades, Euchre, Pinochle |
| `pass_cards` | Implemented | Hearts |
| `draw_discard` | Implemented | Gin Rummy, Golf |
| `meld` | Implemented (set/run) | Gin Rummy, Pinochle, Hand and Foot |
| `poker_betting` | Implemented | Texas Hold'em, Stud, Wilds |
| `showdown` | Implemented | All poker variants |
| `score` | Implemented | All multi-round games |
| `free_play` | Implemented | Free Play mode |
| `war` | Implemented | War |
| `go_fish` | Implemented | Go Fish |
| `blackjack_round` | Implemented | Blackjack |
| `flip_compare_ready` | Implemented | High Card (internal) |
| `flip_compare_result` | Implemented | High Card (internal) |
| `deal` | Implemented | Texas Hold'em, Stud, Euchre (initial deal phase) |
| `name_trump` | Implemented | Pinochle |

### `pass_cards`
All players simultaneously choose cards to pass.
```json
{
  "id": "pass",
  "type": "pass_cards",
  "count": 3,
  "direction": "rotate",
  "targets": ["left", "right", "across", "none"]
}
```
`direction: "rotate"` advances through `targets` each round.

---

### `trick_taking`
Core trick-taking loop. Runs until all hands are empty (or a configured limit).
```json
{
  "id": "play",
  "type": "trick_taking",
  "trump": null,
  "lead_card": "2_of_clubs",
  "follow_suit": true,
  "lead_restrictions": [
    { "suit": "hearts", "until": "hearts_broken" },
    { "card": "Qs", "first_trick": false }
  ],
  "no_points_first_trick": true,
  "left_bower": false,
  "winner": "highest",
  "trick_winner_leads_next": true,
  "collect_tricks_to": "won_tricks",
  "next": "score"
}
```

`trump`: `null` | `"spades"` | `"bid_result"` | `"turn_up"` | `"bidder_choice"`

`winner`: `"highest"` | `"highest_trump_then_lead"`

`left_bower`: Euchre rule — jack of the same-color suit ranks as highest trump

---

### `bidding`
Auction-style bidding round.
```json
{
  "id": "bid",
  "type": "bidding",
  "style": "number",
  "order": "left_of_dealer",
  "min_bid": 0,
  "max_bid": 13,
  "pass_allowed": false,
  "special_bids": ["nil", "blind_nil"],
  "stick_the_dealer": false,
  "going_alone": false,
  "once_around": false,
  "next": "play"
}
```

`style`: `"number"` | `"suit_or_pass"` | `"accept_or_pass"` | `"number_and_suit"`

`bid_increment`: step between numeric bids (default `1`; use `10` for Pinochle).

After number-style bidding completes, `BiddingHandler` writes `bid_winner` (highest bidder's ID) and sets `CurrentPlayerIndex` to that player. Pair with a `name_trump` phase to let the winner choose trump.

Euchre two-round bidding uses two separate `bidding` phases with different styles and conditional `next` links.

---

### `poker_betting`
Standard poker betting round.
```json
{
  "id": "bet_preflop",
  "type": "poker_betting",
  "structure": "no_limit",
  "starting_player": "three_left_of_dealer",
  "can_check": false,
  "next": "flop"
}
```

`structure`: `"no_limit"` | `"limit"` | `"pot_limit"`

`starting_player`: `"left_of_dealer"` | `"two_left_of_dealer"` | `"three_left_of_dealer"` | `"lowest_up_card"` | `"highest_up_cards"`

- `"lowest_up_card"` — player with the lowest face-up card starts (stud 3rd street); ties broken by suit (clubs < diamonds < hearts < spades)
- `"highest_up_cards"` — player with the best showing (pairs first, then high card) starts (stud 4th–7th)

`post_blinds`: `true` — auto-post small and big blinds from the top-level `blinds` definition before the round opens.

`bring_in`: `true` — starting player (`lowest_up_card`) auto-posts a forced bring-in bet; remaining players can call, complete, or fold; bring-in player acts last.

`bring_in_amount`: chip amount of the bring-in. Defaults to the game's `ante.amount`, or 1 if no ante is defined.

**Blinds** (top-level field, consumed by `post_blinds: true`):
```json
"blinds": {
  "small": { "position": "left_of_dealer",     "amount": 1 },
  "big":   { "position": "two_left_of_dealer",  "amount": 2 }
}
```

**Ante** (top-level field — auto-posted for all players before each deal):
```json
"ante": { "amount": 1 }
```

---

### `draw_discard`
One draw + one discard per player turn. Repeats until a special action ends the round.
```json
{
  "id": "play",
  "type": "draw_discard",
  "draw_from": ["deck", "discard"],
  "draw_count": 1,
  "discard_count": 1,
  "target_zone": "grid",
  "special_actions": ["knock", "gin", "go_out"],
  "knock_condition": "deadwood_lte_10",
  "gin_condition": "deadwood_eq_0",
  "go_out_condition": "hand_empty",
  "round_ends_when": "any_player_grid_all_face_up",
  "remaining_players_get_one_more_turn": true,
  "next": "score"
}
```

`target_zone`: `"hand"` | `"grid"` — Grid mode (Golf): drawn card is held in a temporary hand slot; player then taps a grid card to swap it in (drawn card replaces grid card face-up; grid card goes to discard). Player may also `discard_drawn` to skip the swap.

`round_ends_when`: `"any_player_grid_all_face_up"` — round ends when any player has all grid cards face-up.

`remaining_players_get_one_more_turn`: `true` — after round-end trigger, each other player gets one final turn before scoring.

`special_actions`: `["knock","gin","go_out"]` — extra actions shown in discard phase when conditions are met.

`go_out_condition`: `"hand_empty"` | `"all_melds_complete_and_hand_empty"`

`round_ends_when`: also accepts `"stock_exhausted"` — the round ends when the draw pile
runs out, which is how Hand and Foot and Gin Rummy end a round nobody goes out of. Without
it those games simply stopped, with every seat unable to draw and no way forward.

#### Conditional draw sources

An entry in `draw_from` may be an object instead of a zone name. Plain zone names keep
working and mean the same thing, so only the games needing conditions carry them:

```json
"draw_from": [
  { "zone": "deck", "count": 2 },
  { "zone": "discard", "count": "pile",
    "requires": { "all": [
        "team_has_melded",
        { "hand_count_of_rank": "top_discard", "at_least": 2 } ] } }
]
```

`zone` — required. A source naming no zone fails the definition.
`count` — how many cards. Set per zone through `draw_count` (`{ "from_deck": 2,
"from_discard": 7 }`), or `"pile"` for the whole pile.
`requires` — a [condition](#conditions). The source is offered only while it holds.
`then_must` — what drawing here obliges the player to do before the turn can end.
Only `"meld_top_card"` today: the card the pile was claimed for must go down this
turn, which is what stops a conditional pickup being a free fistful of cards. Every
route to a discard is refused while it stands.

#### `unmeldable_ranks`

Ranks that may never be laid in a meld, however many the player holds:

```json
"unmeldable_ranks": ["3"]
```

Hand and Foot's 3s exist to be discarded and score against you. Wild ranks are judged as
wilds, not by their printed rank. An entry that is not a rank fails the definition.

Which ranks are *wild* is not declared here — it comes from `scoring.wild_cards`, so the
cards that score as wilds and the cards that meld as wilds are always the same ones.

#### `initial_meld_requirement`

What a side's *first* meld of a round must be worth before it may lay anything down. Tiers
are matched by round number; a last entry with no `round` is the default:

```json
"initial_meld_requirement": [
  { "round": 1, "points": 50 },  { "round": 2, "points": 90 },
  { "round": 3, "points": 120 }, { "points": 150 }
]
```

The minimum is measured against the cards laid in that one action, valued as `card_points`
scoring values them.

---

### `war`
Each active player flips their top card; highest wins all flipped cards. Ties trigger war.
```json
{
  "id": "battle",
  "type": "war",
  "war_face_down_count": 3,
  "tie_resolution": "war",
  "next": { "if": "win_condition", "then": "end", "else": "battle" }
}
```

---

### `go_fish`
Ask-for-ranks loop. Repeats per player until no cards remain.
```json
{
  "id": "play",
  "type": "go_fish",
  "book_size": 4,
  "collect_to": "books",
  "next": { "if": "win_condition", "then": "end", "else": "play" }
}
```

---

### `blackjack_round`
Full blackjack round: initial deal, player actions, dealer reveal, payout.
```json
{
  "id": "round",
  "type": "blackjack_round",
  "dealer_hits_soft": 16,
  "blackjack_pays": "3:2",
  "allow_split": true,
  "allow_double_down": true,
  "allow_surrender": false,
  "next": { "if": "win_condition", "then": "end", "else": "round" }
}
```

---

### `meld`
Lay-down meld phase. Players may declare melds from their hand to their meld zone.
```json
{
  "id": "meld",
  "type": "meld",
  "meld_types": ["set", "run"],
  "min_meld_size": 3,
  "wilds_allowed": true,
  "max_wilds_per_meld": 1,
  "layoff_allowed": true,
  "next": "play"
}
```

`meld_types`: `"set"` | `"run"` | `"canasta"` | `"pinochle"`

---

### `showdown`
Reveal all remaining hands and determine winner by hand rank.
```json
{
  "id": "showdown",
  "type": "showdown",
  "evaluator": "high_hand",
  "community_zone": "community",
  "hand_size": 5,
  "use_from_hand": { "min": 0, "max": 2 }
}
```

`evaluator`: `"high_hand"` (default) | `"low_hand"` | `"ace_to_five_low"` (Razz — Ace counts as 1, no flush/straight) | `"high_low"` (reserved)

`use_from_hand`: restrict how many hole cards must be used. Omaha: `{min:0, max:2}`. 7-card stud: `{min:5, max:7}`.

**Wild cards** (read from `scoring.wilds`):
```json
"wilds": [{ "rank": "2" }, { "card": "Jh" }]
```
Wild cards are substituted with every possible rank × suit to find the best hand.

`scoring.evaluator` (set by house rules like Razz) overrides the phase-level `evaluator` at runtime.

---

### `deal`
In-round deal phase: burn an optional card then deal to a zone or all players.  Auto-advances.
```json
{
  "id": "flop",
  "type": "deal",
  "burn_first": true,
  "to": "community",
  "count": 3,
  "face": "up"
}
```
```json
{
  "id": "deal_hole",
  "type": "deal",
  "to": "each_player",
  "cards": [
    { "count": 2, "face": "down" },
    { "count": 1, "face": "up" }
  ]
}
```

| Field | Default | Description |
|---|---|---|
| `to` | `"community"` | Zone ID, `"each_player"`, or `"each_active_player"` (skips folded) |
| `count` | `1` | Cards to deal (ignored when `cards` array is present) |
| `face` | `"down"` | `"up"` or `"down"` (ignored when `cards` array is present) |
| `cards` | — | Array of `{count, face}` segments for mixed face-up/down deals |
| `burn_first` | `false` | Discard one card to the `burn` zone before dealing |

---

### `name_trump`
The bid winner (recorded in `bid_winner` metadata by `BiddingHandler`) selects a trump suit. Writes `bid_trump` for `trick_taking` phases that use `"trump": "bid_result"`.

```json
{ "id": "name_trump", "type": "name_trump" }
```

Optional `exclude_suit` parameter excludes one suit from the selection (same format as `bidding.exclude_suit`).

---

### `score`
Calculate and apply scores for the round using the game's `scoring` config, then check the win condition.  Auto-advances after 2.5 seconds (player can also tap).

```json
{ "id": "score", "type": "score", "next": "new_round" }
```

Implemented in `ScoringEngine` + `PhaseHandlerRegistry.ScorePhaseHandler`.

---

### `free_play`
No rules enforced. Players move cards freely between zones.
```json
{
  "id": "play",
  "type": "free_play",
  "end_turn": "manual",
  "end_game": "manual"
}
```

---

## Conditions

Wherever a definition asks a question about the position — `requires` on a draw source, and
anywhere a phase takes `requires` or `when` — it uses this vocabulary. Conditions are JSON,
not a string syntax: there is no parser to write, and every term can be checked when the
definition loads.

A condition is a term name, an object naming a term, or a combinator:

```json
"team_has_melded"
{ "hand_count_of_rank": "top_discard", "at_least": 2 }
{ "all": [ "team_has_melded", { "not": "stock_exhausted" } ] }
```

| Term | Holds when |
|---|---|
| `stock_exhausted` | The draw pile is empty |
| `team_has_melded` | This side has laid anything down this round |
| `hand_empty` | The player to act holds no cards |
| `always` / `never` | Unconditionally true / false |
| `{ "hand_count_of_rank": <rank>, "at_least": n }` | The player holds `n`+ of that rank. The rank is a literal (`"K"`) or `"top_discard"` |
| `{ "meld_value_at_least": n }` | This side's melds are worth `n`+ points, valued as scoring values them |
| `can_open_with_top_discard` | This side has not melded, and could lay an opening worth what this round demands using the top card of the discard |
| `top_discard_is_meldable` | The discard's top card is not a rank the phase bars from melding — a 3 on top freezes the pile |

| Combinator | Meaning |
|---|---|
| `{ "all": [ … ] }` | Every condition holds |
| `{ "any": [ … ] }` | At least one holds |
| `{ "not": … }` | The condition does not hold |

An **absent** condition is true — a rule with no condition applies always. An object naming
no known term is **false**, and fails validation, so it never reaches the table: silently
treating an unreadable rule as satisfied is how a typo becomes a game that plays wrong.

Terms are deliberately few, and each exists because a real game needed it. Adding one is a
small change to `RuleCondition`; adding one nothing needs is how this becomes a language
nobody can hold in their head.

---

## Expressions

Where a definition needs a number that depends on the table, it can state the arithmetic
instead of a fixed value or a tier list. Used today by deck `copies` and `jokers`.

```json
"copies": "players + 1",
"jokers": "(players + 1) * 2"
```

Integers, `+ - * /`, parentheses, and `min(a, b)` / `max(a, b)`. Division truncates.
No variables, no calls, no reference to the position — an expression evaluates identically
on every client, which is what lets two devices deal the same cards from a shared seed.

Named values available: `players`.

An expression that will not parse, or names something unknown, fails the definition.

---

## Not Yet Expressible

Honest limits, so the vocabulary is judged on what it does rather than assumed complete.
Each of these needs a primitive that does not exist; none is hard in itself, and each
should be added when a game actually calls for it — not before.

- **Anything depending on history.** "You may not discard the card you just took",
  "trump cannot be led until it is broken", "a player who passed may not bid again". The
  engine can see the position but keeps no per-turn record to ask questions of.
- **Rules that reference other rules.** "Melds of this rank cannot be extended once
  frozen", "the bonus applies only if the contract was doubled". Conditions read the
  table, not other declarations.
- **Per-card annotations.** Rules attaching state to an individual card — frozen piles,
  captured cards, cards marked during play — beyond the grouping meld zones use.
- **New suits.** Suits restrict to the standard four. A fifth needs artwork first: suits
  are drawn as hand-authored vector paths, not glyphs.
- **Obligations beyond one named kind.** A draw source can demand `then_must:
  "meld_top_card"`, but the obligation vocabulary has exactly that one entry. "You must
  discard a card of the suit led", "you must pass three cards" would each need another.
- **Continuous or simultaneous play.** Every phase assumes turns.

---

## Scoring

All types are dispatched by `ScoringEngine.Apply(state)`, called from the `score` phase handler.

| Type | Status | Games |
|---|---|---|
| `none` | Implemented | War, Go Fish, Free Play |
| `card_points` | Implemented | Hearts |
| `trick_bid` | Implemented | Spades |
| `grid_values` | Implemented | Golf |
| `blackjack` | Implemented | Blackjack |
| `euchre` | Implemented | Euchre |
| `deadwood` | Implemented | Gin Rummy |
| `meld_points` | Implemented | Hand and Foot |
| `pinochle` | Implemented | Pinochle |
| `hand_rank` | No-op | Poker variants (handled by ShowdownHandler) |

### `card_points`
Cards in won zones are worth point values.
```json
"scoring": {
  "type": "card_points",
  "count_from": "won_tricks",
  "card_values": [
    { "suit": "hearts", "value": 1 },
    { "card": "Qs", "value": 13 }
  ],
  "accumulate": true,
  "special": [
    {
      "name": "shoot_the_moon",
      "condition": "all_hearts_and_Qs_in_won_tricks",
      "effect": "add_26_to_others"
    }
  ]
}
```

### `trick_bid`
Score based on tricks bid vs. tricks won.
```json
"scoring": {
  "type": "trick_bid",
  "per_bid_trick": 10,
  "bag_penalty": { "bags_per_penalty": 10, "penalty": -100 },
  "count_by": "team",
  "nil": { "success": 100, "failure": -100 },
  "blind_nil": { "success": 200, "failure": -200 }
}
```

### `euchre`
```json
"scoring": {
  "type": "euchre",
  "makers_win": { "tricks_3_4": 1, "tricks_5": 2 },
  "euchred": { "opponents_score": 2 },
  "loner_win": { "tricks_5": 4 },
  "accumulate": true,
  "count_by": "team"
}
```

### `hand_rank`
Winner is determined by poker hand rank.
```json
"scoring": {
  "type": "hand_rank",
  "evaluator": "high_hand",
  "wilds": []
}
```

### `deadwood`
Gin Rummy — score based on unmelded card values.
```json
"scoring": {
  "type": "deadwood",
  "card_values": { "A": 1, "J": 10, "Q": 10, "K": 10, "default": "pip" },
  "knock_bonus": 25,
  "gin_bonus": 25,
  "undercut_bonus": 25,
  "accumulate": true
}
```

### `blackjack`
Per-hand chip gain/loss vs. dealer.
```json
"scoring": { "type": "blackjack" }
```

### `grid_values`
Golf — sum of card values in grid.
```json
"scoring": {
  "type": "grid_values",
  "card_values": { "A": 1, "2": -2, "3": 3, "J": 10, "Q": 10, "K": 0, "joker": -2, "default": "pip" },
  "matching_columns": { "pair_value": 0 },
  "accumulate": true
}
```

### `none`
No scoring (War, Go Fish — win by other condition).
```json
"scoring": { "type": "none" }
```

---

## Win Condition

All types are implemented in `WinConditionEngine`.

```json
{ "type": "lowest_score",    "threshold": 100 }
{ "type": "highest_score",   "threshold": 500 }
{ "type": "target_score",    "score": 10 }
{ "type": "last_with_cards" }
{ "type": "last_with_chips" }
{ "type": "most_books" }
{ "type": "fixed_rounds",    "count": 9, "winner": "lowest_score" }
{ "type": "manual" }
```

| Type | Trigger | Winner |
|---|---|---|
| `lowest_score` | Any player's score ≥ `threshold` | Lowest score |
| `highest_score` | Any player's score ≥ `threshold` | Highest score |
| `target_score` | Any player's score ≥ `score` | First to reach it |
| `last_with_cards` | Any player has 0 cards | Most cards |
| `last_with_chips` | Only one player has score > 0 | That player |
| `most_books` | Deck + all hands empty | Most books (highest score) |
| `fixed_rounds` | `count` rounds completed | Highest score, or lowest if `winner: "lowest_score"` |
| `manual` | Never (game logic sets `game_over` directly) | — |

---

## House Rules

Each house rule declares an id, display info, default value, and what it overrides in the game definition.

```json
"house_rules": [
  {
    "id": "short_game",
    "name": "Short Game",
    "description": "Game ends at 50 points instead of 100.",
    "default": false,
    "affects": {
      "win_condition.threshold": 50
    }
  },
  {
    "id": "extra_cards",
    "name": "7-card deal",
    "description": "Deal 7 cards instead of 5.",
    "default": false,
    "affects": {
      "deal.cards_per_player": 7
    }
  }
]
```

`affects` is a map of path keys to JSON values. Supported path forms (same as `overrides`):

| Path form | Example | Effect |
|---|---|---|
| `"deck"` | `"standard-52-jokers"` | Replace deck |
| `"teams"` | `{ "count": 2, … }` | Replace teams config |
| `"players"` | `{ "min": 2, "max": 4 }` | Replace players config |
| `"scoring"` | `{ "type": "card_points", … }` | Replace scoring |
| `"win_condition"` | `{ "type": "target_score", "score": 20 }` | Replace win condition |
| `"deal.<field>"` | `"deal.cards_per_player": 7` | Patch a deal field |
| `"win_condition.<field>"` | `"win_condition.threshold": 50` | Patch a win condition field |
| `"scoring.<field>"` | `"scoring.bag_penalty": null` | Set/clear a scoring extra field |
| `"<phaseId>.<param>"` | `"battle.tie_resolution": "split"` | Set a phase extra parameter |

---

## UI Config

Optional `ui` block for display hints.

```json
"ui": {
  "card_scale": 1.2,
  "auto_sort_hand": "rank",
  "allow_sort": true,
  "show_game_log": false
}
```

| Field | Default | Description |
|---|---|---|
| `card_scale` | `1.0` | Scale multiplier on the base card size |
| `auto_sort_hand` | `"none"` | Auto-sort after every action: `"none"` \| `"rank"` \| `"rank_ace_high"` \| `"suit"` |
| `allow_sort` | `true` | Show a Sort button in the HUD |
| `show_game_log` | `true` | Show a Log button in the HUD |

---

## Phase Handler Reference

All phase types are implemented as `IPhaseHandler` subclasses registered in `PhaseHandlerRegistry`. Each handler receives its `PhaseDefinition` (for parameters) and a `nextPhaseId` (the phase to transition to on completion).

| Handler class | Phase type | What It Handles |
|---|---|---|
| `TrickTakingHandler` | `trick_taking` | Lead, follow-suit, trump, trick collection |
| `BiddingHandler` | `bidding` | Auction bidding, pass/accept, stick-the-dealer |
| `PokerBettingHandler` | `poker_betting` | Bet, call, raise, fold, check, all-in |
| `ShowdownHandler` | `showdown` | Multi-hand reveal, rank comparison |
| `BlackjackRoundHandler` | `blackjack_round` | Deal, player actions, dealer AI, payout |
| `DrawDiscardHandler` | `draw_discard` | Per-player draw+discard loop, special actions |
| `MeldHandler` | `meld` | Meld detection (sets, runs, canasta) |
| `GoFishHandler` | `go_fish` | Ask/receive/go-fish loop, book collection |
| `WarHandler` | `war` | Flip, compare, collect with war-on-tie |
| `PassCardsHandler` | `pass_cards` | Simultaneous card passing with direction rotation |
| `FreePlayHandler` | `free_play` | Unconstrained card movement between zones |
| `ScorePhaseHandler` | `score` | Applies the game's `scoring` config at round end |
| `FlipCompareReadyHandler` | `flip_compare_ready` | Each player reveals a card; highest rank wins |
| `FlipCompareResultHandler` | `flip_compare_result` | Winner collects; advance round or end game |

---

## AI Interface

All AI-controlled players implement `IPlayerAgent`:
```csharp
public interface IPlayerAgent
{
    string PlayerId { get; }
    GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions);
}
```

`SmartDefaultAiAgent` is the built-in heuristic agent — registered automatically by `DefaultGameLogic` for every non-human seat (players at index 1+) that doesn't already have an agent registered. It plays trick-taking games with basic strategy (lowest-beater-or-dump, Hearts avoidance), prefers low-rank draw-discard cards, and folds conservatively in poker. `DefaultAiAgent` (pure random) remains available for explicit use.

The engine calls `IGameLogic.GetAutoAction(state)` during auto-advance ticks. When the current player has a registered agent, `GetAutoAction`:
1. If selectable card IDs exist → builds `play_card` actions for each, lets the agent choose.
2. Otherwise → filters valid actions to meaningful types (excludes `"tap"`, `"ai_step"`), lets the agent choose.
3. Falls back to the first valid action for scripted/automated phases.

Game-specific agents (e.g., `GoFishAiAgent`) are registered in the phase handler's `OnGameStart` via `state.PlayerAgents[playerId] = new MyAgent(playerId)`.

---

## Adding a New Game

1. Create `games/<id>.json` using this schema.
2. Add the game id to `GameLoader.GameIds`.
3. If the game needs a new phase type, implement `IPhaseHandler` and register it in `PhaseHandlerRegistry`.
4. If the game needs a new scoring type, add a case to `ScoringEngine.Apply`.
5. Add a help file at `games/help/<id>.md`.
6. The game appears automatically in the game picker — no other app-layer changes needed.

A definition is validated when it loads. An unknown deck name, an unreadable expression, a
draw source naming no zone, or a condition term the engine does not recognise keeps the
game **out of the list entirely**, with the reason recorded in `GameLoader.LoadErrors`.
That is deliberately harsh: the alternative is not a loud failure but a silent one, where
the game appears, deals, and plays with one of its rules simply missing.
