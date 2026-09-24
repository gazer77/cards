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
- There is no per-game C# left. `src/Cards.Core/Logic/` holds one file, `GoFishAiAgent`,
  which is a computer player and not a rule; the legacy WarLogic, BlackjackLogic and
  GoFishLogic went with the games that stopped needing them.

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
- [ ] **One Euchre, with a seat count** — the 3- and 4-player games are separate
      definitions because a definition cannot yet say "this rule depends on how many are
      playing": teams at four and none at three, a different deck at two, and scoring
      that pays the makers differently. `extends` + `overrides` gets one file out of
      another — which is why `euchre-3p.json` is eight lines — but a player still picks
      between two entries in the list instead of picking Euchre and then a number.
      Needs **One game, several configurations** under Definitions; this is its first
      case, and 2-player Euchre above is the second.
      (`LogicRegistry` is empty and every definition runs on `DefaultGameLogic`), but
      Euchre is the game the shared engine knows most about by name, and some of it is a
      game hiding in the vocabulary rather than vocabulary a game uses:
      - `scoring.type: "euchre"` is a scoring method whose *shape* is Euchre's — a maker,
        thresholds at three and five tricks, a loner — parameterised only in what those
        are worth. It even reads two spellings of its own key (`tricks_3_or_4` and
        `tricks_3_to_5`) because the 3- and 4-player files disagree. A declarative
        "score the side that named trump, by tricks taken, against a table of bands"
        would cover it, Spades' contracts and Pinochle's bid alike.
      - `euchre_maker` is a metadata key written by the generic bidding handler. The
        rest of the engine calls the same idea `bid_winner`.
      - `left_bower` is a rule stated as a flag. The rule underneath — a card is promoted
        into another suit for the hand — has no general form, so a game with a different
        promotion cannot say it.
      Not urgent: Euchre plays, and the engine's Euchre-named parameters are read from
      the definition rather than assumed. It is worth naming as debt because each is a
      place where a second game would find the vocabulary shaped around the first.

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
- [ ] **Vote on the house rules** — before a multiplayer game starts, every seat sees the
      rules the definition offers and says yes or no, rather than the host deciding for
      the table. The rules are already declared per game with a name, a description and
      a default (`house_rules`), and a table already agrees on a definition before it
      deals, so the missing part is the asking: a lobby screen listing each rule with a
      tally, a way to close the vote, and the agreed set travelling with the deal so
      every client applies the same patches in the same order. Open questions worth
      settling when it is picked up: whether the host can carry a rule the table voted
      down, whether a majority or unanimity decides, and whether a late joiner inherits
      the vote or reopens it. Local games would get it too, as a way to see what a game
      offers without leaving the table.
- [x] Save and resume any number of games — each save gets its own slot carrying the
      seat count and house rules it was written at, listed for resuming on the setup
      screen. Replaces one-slot-per-game, which let a four-player save load into a
      two-player game and strand cards in hands nobody could reach
- [ ] **Save and resume multiplayer games** — deliberately not attempted yet. Saving is
      only the visible part: a resumed multiplayer game has to re-establish who was in
      which seat, agree with peers on which save is authoritative, and handle players
      who do not come back. That needs stable seat identity and reconnect, which are
      Phase 3 of the web plan and do not exist yet. `SaveSlot` and `GameSaveService`
      are shaped so a multiplayer save is another slot with roster and room information
      attached, not a second mechanism. **Blocked on Phase 3.**
- [x] Hand sort remembered per game — the web client stores the player's choice under
      `sort:{gameId}` and reapplies it each deal; "Free" is remembered too, so a
      hand arranged by hand is not re-sorted underneath the player
### Decks
- [x] Declared deck composition — a game states its ranks, suits, copies and jokers
      rather than picking from a fixed list of names in C#. Copies and jokers scale
      with the table, in the same shape `cards_per_player` uses. The old names remain
      as shorthand, and an unknown deck now fails instead of silently dealing 52
- [ ] **Expressions for deck composition** — Hand and Foot's rule is "one pack per
      player, plus one", which today takes five `max_players` tiers for copies and
      five more for jokers. An expression (`"copies": "players + 1"`) would say it
      once. Wants a small, safe evaluator — no arbitrary code — and the same form
      would suit `cards_per_player` and house-rule overrides
- [ ] **Custom suits** — `Suit` is an enum with four values, and the renderer draws
      each one as a hand-authored vector path (`SuitShapes`). A fifth suit needs
      artwork, a sort order, and a way for scoring rules that name suits to refer to
      it; `DeckSpec` already parses a suit list, so the parsing is the small part
- [ ] **Custom card graphics** — card faces are drawn procedurally, which is why they
      cost nothing to ship and cache well. Player-supplied art means an image
      pipeline, per-card assets, and a fallback when one is missing
- [ ] **Choosable default hand sort** — a settings-screen preference applied to games
      the player has not set individually. Today the fallback is whatever the game
      definition names in `ui.default_sort`, which cannot be overridden globally.
      Wants the same settings screen as animation speed (see Phase 6 in the web plan);
      worth doing as one screen rather than piecemeal. MAUI stores nothing per game
      at all and should adopt `SettingsService.GetHandSort` when it moves onto
      `GameTableViewModel`.


### Definitions
- [ ] **One game, several configurations** — a definition should carry what is common
      (name, help, tags, artwork, the shape of the game) once, and then the parts that
      differ by what is known at setup — the player count first, and later the enabled
      house rules or the chosen deck. Today those are separate games: `euchre-4p` and
      `euchre-3p` are two entries in the picker for one game, and a player chooses
      between them instead of choosing Euchre and then how many are playing.

      A sketch of the shape, to be argued with when it is picked up:

      ```json
      "name": "Euchre",
      "players": { "min": 2, "max": 6 },
      "configurations": [
        { "when": { "players": 4 }, "teams": { "count": 2, "size": 2 },
          "scoring": { "makers_win": { "tricks_3_or_4": 1, "tricks_5": 2 } } },
        { "when": { "players": 3 }, "teams": false,
          "scoring": { "makers_win": { "tricks_3_to_5": 1 } },
          "play": { "loner_skips_partner": false } }
      ]
      ```

      What it needs, roughly in order:
      - Resolution: the matching configurations merge onto the common part once, when
        the game is dealt, using the patch rules `overrides` already has. Most specific
        match wins; no match is a definition error rather than a silent default.
      - Validation of every configuration at load, not only of the one a given table
        picks — a definition whose 6-player rules are malformed should fail when it is
        written, not when six people finally sit down.
      - The setup screen offering the seat range for the game rather than a list of
        games that differ only by a number, and the resume list recording which
        configuration a save was written under.
      - `extends` stays for what it is good at: a genuinely different game built on
        another (poker-wilds on poker). Configurations are for one game played by a
        different number of people.

      Euchre is the case in hand — see the Euchre section — but Golf's grid size, Hand
      and Foot's pack count and Spades' 3-player variant all want the same thing, and
      each is currently either a tier table or a second file.
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
- [x] **Declarative zone layout** — a zone declares a `layout`: a named region or an
      exact `place` in table percentages, written for the bottom seat and turned to
      every other. Every shipped game is on it and the hand-tuned engine is gone.
- [ ] **Player-settable card size** — a definition now states a relative size per
      zone (`card_scale`); the player should be able to scale the whole table
      further from settings — eyesight and screen size vary more than any
      default can cover.
- [ ] **Game manager page** — create, edit, export and import game definitions in the
      app, so the JSON vocabulary is usable without a text editor and a rebuild.
      Depends on the definition validator, which already reports what is wrong with
      a definition and why; an editor is mostly a UI over that. Export/import also
      gives players a way to share a game they wrote.
- [ ] **Declared but not implemented** — the audit (`DefinitionAudit`, warnings in
      `GameLoader.LoadWarnings`) now names every definition property nothing reads, and
      `DefinitionAuditTests` pins today's list so no new one can join it quietly. What
      is on that list and worth doing:
      - Blackjack's `allow_split`, `allow_double_down`, `allow_surrender` and
        `blackjack_pays` — four real rules the definition states and the table does not
        offer. Its `roles` block (a dealer seat with fixed rules) is the same story.
      - `dealer: "random"` on ten games — who deals first. The engine always starts at
        seat 0 and rotates.
      - `deal.order` and a bidding phase's `order` ("clockwise", "left_of_dealer") —
        descriptive today; the deal and the bidding are both hard-wired to go clockwise
        from the dealer's left.
      - Hand and Foot's `locked_until` on the foot, Pinochle's `scoring.meld_table`,
        Euchre's bidding `prompt`.
      Each is either a rule to implement or a line to delete; leaving them stated and
      unread is the one option now ruled out.
- [ ] **Hand and Foot at five seats** — the two side players' feet are drawn at the
      smallest size the table allows. Five seats put three players along the top, which
      leaves the sides a narrow band, and Hand and Foot asks each seat for a hand, a
      foot and a meld strip. Playable, but the foot is a token rather than a pile.
- [x] **Score card zone** — a `score_card` block places a panel with a row per player or
      team, a total, and a detail view with a column per round. The engine records what
      each round scored (`GameState.ScoreHistory`), so detail and total always agree.
      Golf and Hearts have one; the rest may declare one whenever it earns its space.
- [ ] **Scoring with cards** — the Euchre way: a side keeps score by exposing pips on
      a pair of low cards (a 6 and a 4, say), turning and covering them as points come.
      Declared as a scoring zone per team whose cards are set aside from the deck at the
      deal and whose face/orientation is driven by the score, so the definition — not
      code — says which cards, how many points each configuration shows, and where they
      sit on the table. Would also cover Cribbage-style peg boards drawn as cards later.
- [ ] **Collapsible table elements — detail and total** — anything placed on the
      table that can grow (the score card first, meld spreads and badges later) should
      declare a collapsed and an expanded form, with a tap to switch. For scores that
      means two views of the same figures: *total* — one number per player, always in
      view — and *detail* — a column per round with the total at the end. Golf shows
      why: nine holes per player is too much to leave open, yet the running total alone
      hides who won which hole. The definition names both views and which one a game
      starts in; the choice is per-viewer, and the collapsed form still has a `place`.

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
| Game manager page (create/edit/export/import) | Medium | Definitions are already data and validated on load, so the engine side is done; the work is an editor UI, file import/export on each platform, and deciding where user-authored games live alongside the shipped ones |
| Player-settable card size per zone | Low-Medium | Zone layout already computes per-zone card width, so the plumbing exists; needs a definition field, a settings multiplier, and a decision about how the two compose |
| Accessibility | High | Colorblind palette and font scaling; touches `CardRenderer` and every XAML page |
| Learn-to-play mode | High | Content work on top of the existing help framework |
| Android release prep | Medium | Signing, store listing, device testing |
