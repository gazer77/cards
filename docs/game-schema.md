# Game Definition Schema — cards-game/v1

Games are defined as JSON files loaded at startup. The engine reads them to drive a state machine. Complex logic is handled by named **logic modules** (C# classes registered in the engine). Simple games may need no custom C# at all.

---

## Top-Level Structure

```json
{
  "$schema": "cards-game/v1",
  "id": "hearts",
  "name": "Hearts",
  "version": "1.0",
  "extends": null,
  "deck": "standard-52",
  "players": { "min": 4, "max": 4 },
  "teams": false,
  "dealer": "random",
  "rounds": { "repeat_until": "win_condition", "dealer": "rotates_left" },
  "zones": [...],
  "deal": {...},
  "phases": [...],
  "scoring": {...},
  "win_condition": {...},
  "house_rules": [...],
  "help": "hearts.md"
}
```

### `extends`
Inherit another game's definition and override specific fields. Useful for variants.
```json
"extends": "texas-holdem",
"overrides": {
  "scoring.wilds": [{ "rank": "2" }]
}
```

---

## Deck

| Value | Description |
|---|---|
| `"standard-52"` | Standard 52-card deck |
| `"standard-52-jokers"` | Standard 52 + 2 jokers |
| `"euchre-24"` | 9–A of all 4 suits (24 cards) |
| `"pinochle-48"` | 9–A of all 4 suits × 2 (48 cards) |
| `"standard-104"` | Two standard decks shuffled together |

Custom deck:
```json
"deck": {
  "type": "custom",
  "suits": ["spades", "hearts", "diamonds", "clubs"],
  "ranks": ["9", "10", "J", "Q", "K", "A"],
  "copies": 1
}
```

---

## Players

```json
"players": { "min": 2, "max": 6 }
```

---

## Teams

```json
"teams": false
```
```json
"teams": {
  "count": 2,
  "size": 2,
  "arrangement": "alternating"
}
```

`arrangement` values: `"alternating"` (0,2 vs 1,3), `"sequential"` (0,1 vs 2,3)

---

## Dealer

```json
"dealer": "random"
"dealer": "high_card"
```

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

`first_dealer`: `"random"` (default) | `"high_card"` (not yet implemented)

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
| `grid` | Fixed N×M grid of cards (Golf) |
| `pot` | Virtual zone for chips/point tracking |

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

### `deal`
Deals cards from the deck to a target zone.
```json
{
  "id": "flop",
  "type": "deal",
  "burn_first": true,
  "to": "community",
  "count": 3,
  "face": "up",
  "next": "bet_flop"
}
```
`to`: `"each_player"` | `"community"` | any zone id

---

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

`starting_player`: `"left_of_dealer"` | `"two_left_of_dealer"` | `"three_left_of_dealer"`

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
  "target_zone": "hand",
  "special_actions": ["knock", "gin"],
  "knock_condition": "deadwood_lte_10",
  "gin_condition": "deadwood_eq_0",
  "next": "score"
}
```

`target_zone`: `"hand"` | `"grid"` (Golf swaps into a grid slot)

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
  "use_from_hand": { "min": 0, "max": 2 },
  "next": { "if": "win_condition", "then": "end", "else": "deal_new_round" }
}
```

`evaluator`: `"high_hand"` | `"low_hand"` | `"high_low"`

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

## Scoring

All types are dispatched by `ScoringEngine.Apply(state)`, called from the `score` phase handler.

| Type | Status | Games |
|---|---|---|
| `none` | Implemented | War, Go Fish |
| `card_points` | Implemented | Hearts |
| `trick_bid` | Implemented | Spades |
| `grid_values` | Implemented | Golf |
| `euchre` | Stub | Euchre |
| `hand_rank` | Stub | Poker variants |
| `deadwood` | Stub | Gin Rummy |
| `blackjack` | Handled by BlackjackLogic | Blackjack |

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
    "id": "shoot_moon_subtract",
    "name": "Shoot the Moon Subtracts",
    "description": "Shooting the moon subtracts 26 from your score instead of adding to all others.",
    "default": false,
    "affects": {
      "scoring.special[shoot_the_moon].effect": "subtract_26_from_self"
    }
  }
]
```

`affects` is a map of JSON-path-style keys to override values. The engine applies these when the house rule is enabled.

---

## Logic Modules Reference

These are named C# implementations the engine calls by type string. Each is registered in the engine at startup.

| Module | Games | What It Handles |
|---|---|---|
| `trick_taking` | Hearts, Spades, Euchre, Pinochle | Lead, follow-suit, trump, trick collection |
| `bidding` | Spades, Euchre, Pinochle | Auction bidding, pass/accept, stick-the-dealer |
| `poker_betting` | All poker | Bet, call, raise, fold, check, all-in |
| `hand_rank` | All poker | Hand evaluation high card → royal flush, with wilds |
| `blackjack_round` | Blackjack | Deal, player actions, dealer AI, payout |
| `draw_discard` | Gin Rummy, Golf | Per-player draw+discard loop, special actions |
| `meld` | Gin Rummy, Pinochle, Hand and Foot | Meld detection (sets, runs, canasta, pinochle melds) |
| `go_fish` | Go Fish | Ask/receive/go-fish loop, book collection |
| `war` | War | Flip, compare, collect with war-on-tie |
| `pass_cards` | Hearts | Simultaneous card passing with direction rotation |
| `showdown` | All poker | Multi-hand reveal, rank comparison, pot split |
| `free_play` | Free Play | Unconstrained card movement between zones |
| `score` | All | Applies the game's `scoring` config at round end |

---

## AI Interface

The AI does not need game-specific knowledge from the schema. The engine exposes a `GetLegalActions(gameState)` call. The AI picks from that list based on difficulty. Game-specific strategy (Insane difficulty) is implemented per-game in C# and references the same game definition for context (e.g., what trump is, what the scoring table looks like).

---

## Adding a New Game

1. Create `games/<id>.json` using this schema.
2. If the game uses a logic module not yet implemented, add it to the engine.
3. Add a help file at `games/help/<id>.md`.
4. The game appears automatically in the game picker — no code changes needed for the app layer.
