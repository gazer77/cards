# Cards — Mobile Card Game Platform

A mobile app containing many different card games, all in one place.
If a game is played with cards, this app should have it.

> **Status audited 2026-08-27** against the tree at `a6b5b30` (last code commit 2026-04-12).
> `[x]` = built and wired up. `[ ]` = not built, or only partly built — partial items say
> what exists today.

---

## Tech Stack

- **Framework:** .NET MAUI (C#) — Android-first, iOS-ready when a Mac is available
- **Game Table Rendering:** SkiaSharp canvas (`Views/GameTableView.cs`) — MonoGame was not needed
- **App UI (menus, settings, rules):** MAUI widgets + XAML pages
- **Multiplayer Backend:** raw TCP with length-prefixed message framing
  (`Networking/TcpTransport.cs`) plus room codes — **not** SignalR; see Phase 5
- **Local/Offline Multiplayer:** not started

---

## Architecture (as built)

The engine ended up **declarative**, which the original plan did not anticipate and which
changes what "add a game" means:

- Every game is a JSON definition in `games/` conforming to `docs/game-schema.md`
  (cards-game/v1). 16 definitions ship today.
- `LogicRegistry` is **empty**. All games run through `DefaultGameLogic` +
  `PhaseHandlerRegistry`, which composes 17 reusable phase handlers: `deal`,
  `trick_taking`, `bidding`, `name_trump`, `dealer_discard`, `pass_cards`, `draw_discard`,
  `meld`, `poker_betting`, `showdown`, `war`, `blackjack_round`, `go_fish`, `free_play`,
  `score`, `flip_compare_ready`, `flip_compare_result`.
- Scoring is likewise data-driven (`ScoringEngine`): `card_points`, `trick_bid`, `euchre`,
  `hand_rank`, `deadwood`, `blackjack`, `grid_values`, `none`.
- House rules are JSON patches over a game definition (`HouseRuleEngine`), applied to a
  clone at setup time.
- The legacy per-game C# in `src/Cards/Logic/` (`WarLogic`, `BlackjackLogic`, `GoFishLogic`)
  is superseded by the declarative path and is dead except `GoFishAiAgent`.

**Consequence:** most new games are a JSON file, not a code change. New games only need C#
when they require a phase type that doesn't exist yet.

---

## Games

### Poker
- [x] Texas Hold'em — `games/texas-holdem.json`
- [x] Stud — `games/poker-stud.json`
- [x] Wilds — `games/poker-wilds.json` (one-eyed-jacks house rule)
- [ ] Trips or Better
- [ ] Follow the Queens / Kings — needs dynamic wilds; `scoring.wilds` is static today
- [ ] Low Hole — needs per-player wilds; `scoring.wilds` is static today
- [ ] Blind Baseball

### Euchre
- [x] 4-player — `games/euchre-4p.json`
- [x] 3-player — `games/euchre-3p.json`
- [ ] 2-player

### Other Games
- [x] Hand and Foot
- [x] Pinochle
- [x] War
- [x] Blackjack
- [x] Gin Rummy
- [x] Go Fish
- [x] Hearts
- [x] Spades
- [x] Golf
- [x] High Card *(not in the original plan; simple flip-compare game)*
- [x] Free Play *(not in the original plan; sandbox table with no rules enforcement)*
- [ ] Cribbage — rules text exists at `games/help/cribbage.md`, no game definition
- [ ] Whist — rules text exists at `games/help/whist.md`, no game definition

> Note: `cribbage.md` and `whist.md` were produced as a side effect of `tools/ExtractHoyle`,
> which scrapes the Gutenberg Hoyle text into `games/help/`. They are not evidence of
> in-flight work on those two games.

---

## Features

### Multiplayer
- Internet: friends-only via shareable room codes — *partially built* (see Phase 5)
- Local Network (LAN): TCP transport exists, **no discovery** — host IP must be known
- Local Offline: Bluetooth / Wi-Fi Direct — not started

### AI Opponents
Only one AI exists: `SmartDefaultAiAgent`, a heuristic agent auto-assigned to every non-human
seat. It has trick-taking lead/trump awareness, Hearts point-avoidance, draw-vs-discard
heuristics, and conservative poker betting; everything else falls through to random.
**There is no difficulty setting anywhere in the app.**

- [x] Baseline heuristic AI (`SmartDefaultAiAgent`) + per-game override hook (`GoFishAiAgent`)
- [ ] Easy — erratic, makes frequent mistakes (`DefaultAiAgent` exists but is unreachable)
- [ ] Normal — plays valid moves, low chance of error
- [ ] Hard — optimal valid play, no mistakes, no strategy
- [ ] Insane — no mistakes and uses game-specific strategy

### Customization
- [x] Multiple deck styles — `SkinFactory` serves `simple` and `classic`, both drawn
      procedurally in Skia; no image assets
- [ ] Multiple table backgrounds — `ITableTheme` exists but `DefaultTableTheme`
      (casino green) is the only implementation, and nothing constructs another
- [ ] Player-uploaded custom backgrounds — no file/media picker in the app
- [x] Custom house rules (per-game toggles) — 15 of 16 games define house rules
- [x] Hand sort remembered per game — the web client stores the player's choice under
      `sort:{gameId}` and reapplies it each deal; "Free" is remembered too, so a
      hand arranged by hand is not re-sorted underneath the player
- [ ] **Choosable default hand sort** — a settings-screen preference applied to games
      the player has not set individually. Today the fallback is whatever the game
      definition names in `ui.default_sort`, which cannot be overridden globally.
      Wants the same settings screen as animation speed (see Phase 6 in the web plan);
      worth doing as one screen rather than piecemeal. MAUI stores nothing per game
      at all and should adopt `SettingsService.GetHandSort` when it moves onto
      `GameTableViewModel`.

### Learning & Rules
- [x] Rules reference for every game — `HelpPage` + `games/help/*.md`
      (gap: `high-card.json` has no help file)
- [ ] Learn-to-play mode — not started; nothing in the codebase references it

---

## Task List

### Phase 1 — Foundation — **done**
- [x] Set up .NET MAUI project (Android target)
- [x] Integrate SkiaSharp canvas for game table rendering
- [x] Build core card engine (deck, hand, deal, shuffle) — `DeckBuilder` supports
      `standard-52`, `standard-104`, `standard-52-jokers`, `euchre-24`, `pinochle-48`
- [x] Build card rendering system — face, back, flip/deal/bump/fly-in/riffle animations,
      drag-and-drop with face-correct drag ghost
- [x] Design and build app shell — `HomePage`, `GameSetupPage`, `GameTablePage`,
      `SettingsPage`, `HelpPage`, `LobbyHostPage`, `LobbyJoinPage`
- [x] Implement deck style asset system — procedural skins, not swappable image assets
- [ ] Implement table background system + custom image picker — **not done** (one hardcoded theme)
- [x] Build rules content framework (scrollable markdown pages per game)

### Phase 2 — First Games (Simple) — **done**
- [x] War
- [x] Go Fish
- [x] Blackjack (player vs dealer)
- [x] Hearts
- [x] Spades
- [x] Gin Rummy

### Phase 3 — AI System — **mostly not started**
- [x] Design AI player interface/framework (`IPlayerAgent`, per-seat override in `GameState.PlayerAgents`)
- [ ] Easy AI (random valid moves, frequent errors)
- [ ] Normal AI (best valid move, low error rate)
- [ ] Hard AI (optimal play, no errors, no strategy)
- [ ] Insane AI (per-game strategy engines)
- [ ] Difficulty selection in setup UI and `SettingsService`

### Phase 4 — Complex Games — **mostly done**
- [x] Poker hand evaluator — in `ShowdownHandler`: best-of-N, wild substitution,
      ace-to-five low, and constrained (hole + board) evaluation
- [x] Texas Hold'em
- [ ] Remaining poker variants — stud and wilds ship; trips-or-better, follow-the-queen/king,
      low-hole, and blind baseball do not
- [x] Euchre 4-player
- [x] Euchre 3-player
- [ ] Euchre 2-player
- [x] Pinochle (48-card deck, meld scoring, bidding)
- [x] Hand and Foot
- [x] Golf

### Phase 5 — Multiplayer — **scaffolded, not finished**
- [ ] SignalR game server (ASP.NET Core) — **direction changed**: `GameServer`/`GameClient`
      run peer-hosted over `TcpTransport` with `RoomCode`, heartbeats, and disconnect
      broadcast. No hosted server, so no NAT traversal for internet play.
- [ ] Internet multiplayer — lobby create/join UI and state sync exist; late-join/reconnect
      state-sync messages are defined but the reconnect path is not driven end to end
- [ ] LAN multiplayer: mDNS device discovery — **not built**; only a `GetLocalIp()` helper
- [ ] Offline local multiplayer: Bluetooth / Wi-Fi Direct
- [ ] Player profiles — only a player-name string in `SettingsService`; no avatar

### Phase 6 — Polish & Release
- [x] Custom house rules (per-game rule toggles)
- [x] Card animations — deal slide, flip, receive bump, fly-in, riffle shuffle
- [ ] Sound effects — `SoundService`/`SoundGenerator` play four procedural cues
      (deal, flip, win, lose); no real sound assets, no per-event coverage
- [ ] Accessibility (colorblind mode, font size options) — **nothing implemented**
- [ ] Android release prep (Play Store listing, signing, testing)
- [ ] *Future:* iOS build when Mac access is available

---

## Built but never planned

Work that exists in the app and is worth tracking:

- **Save / resume** — `GameSaveService` snapshots and restores full game state; the home
  screen shows Resume vs. New Game per game
- **Free Play mode** — unruled sandbox table
- **In-game log** — running event log surfaced on the table
- **Poker showdown reveal** — timed hand reveal, Ready gate, auto-ready setting
- **Per-player trick zones + direction-of-play indicator**
- **Card info tooltip on tap**
- **`docs/game-schema.md`** — full spec for the JSON game format
- **`tools/ExtractHoyle`** — scrapes the Gutenberg Hoyle text into `games/help/*.md`

---

## Remaining Risk

Only the unbuilt work; everything above marked `[x]` is settled.

| Remaining work | Risk | Notes |
|---|---|---|
| Cribbage | Medium | Needs a new `pegging` phase type and crib/show scoring — the first real engine extension in a while |
| Whist | Low | Fits the existing `trick_taking` + `card_points` handlers; likely JSON-only |
| Euchre 2-player | Low | JSON variant of the existing euchre definitions |
| Trips or Better, Blind Baseball | Low-Medium | Mostly expressible in the existing poker phases |
| Follow the Queens/Kings, Low Hole | Medium | Requires dynamic and per-player wilds; `scoring.wilds` is static and `ShowdownHandler` resolves it once |
| AI difficulty tiers | Medium | Framework is ready; needs an error-injection wrapper plus a difficulty setting threaded from setup |
| Insane AI (per-game strategy) | Low-Medium feasibility | Per-game research; do it last, one game at a time |
| Internet multiplayer over NAT | Medium-High | Peer-hosted TCP cannot traverse NAT — needs a relay/hosted server or a fallback to the original SignalR plan |
| LAN discovery (mDNS) | Medium | Well-supported on Android; replaces manual host-IP entry |
| Offline local (Bluetooth) | Medium | Android Nearby Connections API |
| Table themes + custom backgrounds | High | `ITableTheme` already abstracts it; needs more implementations, a picker, and image storage |
| Accessibility | High | Colorblind palette and font scaling; touches `CardRenderer` and every XAML page |
| Learn-to-play mode | High | Content work on top of the existing help framework |
| Android release prep | Medium | Signing, store listing, device testing |
