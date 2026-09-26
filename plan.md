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
- **Multiplayer Backend:** a SignalR table server (`src/Cards.Server`) that runs the game
  and sends each seat its own view; see Phase 5 and `docs/shared-tables.md`. The older
  peer-hosted TCP path (`Networking/TcpTransport.cs`) remains in the phone app for now
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
- [x] One game, three variants — `games/poker.json`, with Texas Hold'em as the shape
      the file is written in and Deuces Wild and Seven-Card Stud as named shapes beside
      it. Replaces `texas-holdem.json`, `poker-wilds.json` and `poker-stud.json`
- [ ] Trips or Better
- [ ] Follow the Queens / Kings — needs dynamic wilds; `scoring.wilds` is static today
- [ ] Low Hole — needs per-player wilds; `scoring.wilds` is static today
- [ ] Blind Baseball

### Euchre
- [x] One game, 3 or 4 players — `games/euchre.json`, with a configuration per seat
      count. Partners at four and none at three, each shape scoring by its own rules,
      and going alone skipping a partner only where there is one. Replaces
      `euchre-4p.json` and `euchre-3p.json`, which were one game written twice
- [ ] 2-player — a third shape, once the two-handed rules are settled (a 24-card deck
      is thin for two, and most tables play with a stripped kitty or a dummy hand)

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
- Internet / LAN: friends-only via five-letter table codes — **working on the web** (see Phase 5)
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
- [ ] **Backlog: save and resume multiplayer games** — now unblocked. Seats have stable identity
      (a token per seat) and reconnect works, and the server holds the only copy of a
      game, so "which save is authoritative" has one answer. What remains: the server
      writing rooms to disk (the save shape plus the roster and tokens) so a restart
      does not end every game, and a way for the host to reopen one later with the same
      people.
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
- [ ] **One game, several configurations** — a definition carries what is common once
      and then the parts that differ by what is known at setup. **Built** (see
      `docs/game-schema.md`), and Euchre and poker are both on it: Euchre a shape per
      seat count, poker three named shapes with a picker on the setup screen.
      What is left:
      - The resume list showing which shape a save was written under. The save records
        it and resumes correctly; the list does not say it.
      - Golf's grid size, Hand and Foot's pack count and Spades' 3-player rules, each a
        tier table or a hard-wired default today.

      The shape as built:

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

      What it does to the catalogue: sixteen entries are already fifteen, and become
      about thirteen once poker follows. A save records which shape it was written
      under, so resuming a Stud game does not deal Hold'em.

      `extends` stays for building one definition on another file; whether it survives
      at all is worth asking now that configurations exist, since poker-wilds on
      texas-holdem is exactly a variant by another route.
- [ ] **Swapping the language** — every sentence the table says already goes through
      `GameText`, keyed, with a definition able to replace any of them. That is most of
      the foundation; what is missing is a way to hold more than one language at a time
      and a way to be sure a new sentence does not ship untranslated. Worth settling as
      a practice before the catalogue grows, because the cost of retrofitting is the
      whole catalogue.

      Where the words are today, and what each needs:
      - **Engine messages and buttons** (`GameText.MessageKeys` / `ActionKeys`) — a
        catalogue per language, keyed the same way. A definition's `text` block becomes
        per-language too (`"text": { "en": {…}, "fr": {…} }`, or a file beside the game),
        so a translated game and a reworded one are the same mechanism.
      - **Card names** — `GameText.CardName` builds "Jack of Clubs" from a rank table and
        the suit enum's own name. Neither survives translation; both want a per-language
        table, and the sentence order ("Jack of Clubs" vs "valet de trèfle") wants the
        card name to be one lookup rather than two joined by a word.
      - **Definition literals** — zone labels, badge `zero` text, score card headings,
        house rule names and descriptions, the game's own name. Each is a literal in the
        JSON today. Either they become keys, or the per-language `text` block covers
        them; pick one and apply it everywhere rather than half each.
      - **Help files** — `games/help/*.md` per language.
      - **App chrome** — the setup, settings and menu wording in the Blazor pages and the
        MAUI XAML, which is the one part that has never been keyed at all.
      - **Numbers and plurals** — "{count} cards" has no plural form, and "{player}'s
        turn" is a possessive that several languages do not build that way. The rule that
        keeps this sane: a key is a whole sentence, never a fragment assembled in code.
        It mostly holds today; the exceptions are worth finding before they multiply.

      The practice that makes it stick: a test asserting every key in the default
      catalogue exists in every shipped language, so an untranslated string fails the
      build rather than appearing in English in the middle of a French table. The same
      test the message keys already have, widened. A language setting beside the others,
      with a fallback chain of chosen → English → the key itself, so a missing entry
      degrades to something a person can still act on.
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

### Phase 5 — Multiplayer — **web shared tables working; phone and polish to come**
See `docs/shared-tables.md`.
- [x] SignalR table server (`src/Cards.Server`, ASP.NET Core) — server-authoritative: the
      game runs only on the server, which checks every move against the seat
      (`SeatGate`) and sends each seat its own view (`TableProjection`) with the cards it
      could not see replaced by aliased backs. Serves the web app too; runs on your own
      hardware on port 5280. Rooms by five-letter code, seats held by a device token so a
      reload or a sleeping phone rejoins the same chair. Computer players take open seats
      and the seats of anyone who leaves.
- [x] Seat-relative table — every seat sits at the bottom of its own screen, reads the
      table's lines as addressed to it ("Your turn"), and sees its own row on the score
      card (`GameState.ViewerId`, `GameText.Render`/`PerViewer`).
- [x] Web client — Play with friends from setup, join by code from home, lobby with
      seats and Deal, the table driven by server views through the same view model and
      gestures as a local game (`RemoteGameLogic`, `TableConnection`).
- [x] **Go Fish at a shared table** — the handler is symmetric now: every seat asks the
      same way, the asker chooses whom to ask ("Ask Ana for Kings"), the table's memory
      of what each seat asked for and refused is per seat and public, and Go Fish seats
      two to six. Its lines read three ways (`GameText.Between`): "You asked Bo…",
      "Ana asked you…", "Ana asked Bo…".
- [x] **A dropped player's turn** — the host picks a timeout (30 s to 5 min, or never).
      Past it, on the dropped player's turn, everyone still connected votes whether the
      computer should stand in; a majority hands the seat over, and it is handed back the
      moment the player reconnects. A "wait" majority closes the vote until another
      timeout passes.
- [x] **Speech bubbles beside the right seat** — each line records who it is about as it
      is written (`GameText.SubjectOf`); instructions and summaries are never bubbled, and
      a bubble carries only what was said, not the "Tap to …" after it. Also fixes the
      same misplacement at a single-player table.
- [ ] **Backlog: phone app on shared tables** — `TableConnection` lives in `Cards.App` and
      the SignalR client runs on MAUI; the MAUI pages need the lobby and the view-driven
      table. The old peer-hosted `GameServer`/`GameClient` over `TcpTransport` should then
      be retired.
- [ ] **Backlog: shared games survive a restart** — see "Save and resume multiplayer
      games" under Customization.
- [ ] **Simultaneous choices** — phases where everyone decides at once (Hearts' pass) run
      seat by seat today; at a shared table they could run together.
- [ ] **Private notes in state** — the projection strips the notes it knows name a card
      only the actor has seen (`selected_card`, `dd_drawn_card`); new handler notes need
      the same care, and an audit that fails on a card id in metadata would enforce it.
- [ ] LAN multiplayer: mDNS device discovery — **not built**; with the server on the LAN,
      discovery would find it rather than a peer host
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
- [x] **Blackjack plays the game it declares** — chips (`starting_score` 100, a `bet`
      per hand), a real dealer seat from `roles` (one player is now one player and a
      dealer), every seat settled rather than only the first, split into a second
      hand, double down, surrender (house rule), a natural paid at `blackjack_pays`, a
      natural no longer skips the other seats, and the computer players follow basic
      strategy instead of choosing at random.
- [ ] **Declared but not implemented** — the audit (`DefinitionAudit`, warnings in
      `GameLoader.LoadWarnings`) now names every definition property nothing reads, and
      `DefinitionAuditTests` pins today's list so no new one can join it quietly. What
      is on that list and worth doing:
      - `dealer: "random"` on ten games — a duplicate of `rounds.first_dealer`, which the
        engine does read and which already defaults to random. These lines should be
        deleted rather than implemented.
      - `deal.order` and a bidding phase's `order` ("clockwise", "left_of_dealer") —
        descriptive today; the deal and the bidding are both hard-wired to go clockwise
        from the dealer's left.
      - Hand and Foot's `locked_until` on the foot, Pinochle's `scoring.meld_table`,
        Euchre's bidding `prompt`.
      Each is either a rule to implement or a line to delete; leaving them stated and
      unread is the one option now ruled out.
- [x] **Hand and Foot's computer players meld** — they pick cards and press Lay the way a
      person does: open when the round's minimum can be met (pairs take a wild only
      when the opening needs the points), add naturals to melds already down, put wilds
      where they finish or extend a dirty book, keep a card to discard, and throw black
      threes before anything else. Games against them end at every table size from two
      to six. Found on the way: a filed red three counted as the side having opened, so
      it waived the minimum; the agent's masked view dropped meld groups; and a player
      could meld their last card short of the books and be left with no move.
      Not yet: the computer player does not weigh whether taking the pile is worth it
      beyond the existing rule, and never holds back a meld to go out in one turn.
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
