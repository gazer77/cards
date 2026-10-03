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
- [x] Cribbage — two or three players, to 121 (house rule: 61): laying away to the crib, the cut and his heels, pegging to 31 with goes and the last card, and the show with the crib for the dealer. Four new phase types (`crib_discard`, `cut`, `pegging`, `show`) with every point value in the definition; the counting in `CribbageScore`. The game ends the moment someone pegs out. Not yet: four-player partnerships, and a drawn cribbage board (the score card shows the pegs).
- [x] Whist — partnerships, the last card dealt turned up for trumps (`trump: "last_dealt"`), a point a trick over six (`tricks_over_book`), first dealer by low card, to 7 (Short Whist house rule: 5). Honours are not counted.

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
Chosen per game on the setup screen; a shared table's host chooses it for the room. See
"Difficulty" in docs/game-schema.md.

- [x] Baseline heuristic AI (`SmartDefaultAiAgent`) + per-game override hook (`GoFishAiAgent`)
- [x] Easy — careless: picks at random at 45% of its real decisions (`EasyPlayer`)
- [x] Normal — the baseline. Two blind spots fixed on the way: it read only seat 0's card
      in a trick, and took any game where a heart had been played for Hearts and played
      to lose tricks
- [x] Hard — card memory, master leads, partner play, cheap ruffs; Hearts pass and void
      play; Cribbage discards over every starter. Measured: Hearts 28 points a game to
      normal's 76, Euchre 20/40 to 16, Cribbage 22/40 to 18; Spades and Whist level
- [ ] Hard at Spades and Whist — level with normal today. A sharper bid (counting sure
      tricks) measured worse against these opponents; needs a look at bags and sets
- [ ] Hard for Golf, Gin Rummy, Hand and Foot, Poker, Blackjack, Go Fish — these play
      as normal at hard
- [x] Pacing: a computer turn of many steps flows as one move (`ContinuesMove`): after
      the draw in Hand and Foot, Gin and Golf; picks for a meld, a pass or the crib.
      Hand and Foot's average computer turn went from 8.5 s to 3.6 s (worst 37 s to
      11.5 s), Pinochle's meld from 22 s to 11 s. `PacingTests` holds the ceilings.
- [ ] Pinochle's computer lays its meld a card at a time, every card it holds, since
      one card passes as a meld there — look at what it should actually be laying
- [ ] Pinochle with computer players runs long — they bid high and are set, so games
      can take a hundred deals. Bidding against the meld they hold would fix it
- [ ] Insane — no mistakes and uses game-specific strategy

### Customization
- [x] Multiple deck styles — `SkinFactory` serves `simple` and `classic`, both drawn
      procedurally in Skia; no image assets
- [ ] Multiple table backgrounds — `ITableTheme` exists but `DefaultTableTheme`
      (casino green) is the only implementation, and nothing constructs another
- [ ] Player-uploaded custom backgrounds — no file/media picker in the app
- [x] Custom house rules (per-game toggles) — 15 of 16 games define house rules
- [x] **Vote on the house rules** — in the lobby every person seated has a ballot on each
      rule, with a live tally, and the deal uses what carries. The host's setup choices
      are their ballot; everyone else starts from the rules' defaults; computer seats do
      not vote. How it is decided is in the definition (`house_rule_vote`): `decide_by`
      majority (default, a tie is no) or unanimous, and `host_override` (default no) to
      let the host set a rule always in or out. Voting stays open until the deal, so a
      late joiner simply votes; there is no separate close.
- [x] Save and resume any number of games — each save gets its own slot carrying the
      seat count and house rules it was written at, listed for resuming on the setup
      screen. Replaces one-slot-per-game, which let a four-player save load into a
      two-player game and strand cards in hands nobody could reach
- [ ] **Backlog: save and resume multiplayer games** — rooms are written to disk and
      survive a restart (below). What remains: a way for the host to reopen a closed or
      finished table later with the same people.
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

### Phase 3 — AI System — **easy, normal and hard**
- [x] Design AI player interface/framework (`IPlayerAgent`, per-seat override in `GameState.PlayerAgents`)
- [x] Easy AI (`EasyPlayer`: careless at decisions)
- [x] Normal AI (`SmartDefaultAiAgent`)
- [x] Hard AI (`SmartDefaultAiAgent.Hard`: trick games and Cribbage)
- [ ] Insane AI (per-game strategy engines)
- [x] Difficulty selection in setup UI and `SettingsService`, and for a shared room by its host

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
      same misplacement at a single-player table. `StatusSubjectTests` plays every game
      at its smallest and largest table and fails on any line that never said whose it
      is; the last few (melds laid, the Euchre dealer's discard, ties and team wins) now do.
- [x] **Hosting split from the server** — rooms, seats, votes and the game loop live in
      `Cards.Hosting` (plain .NET: `ITableClients` to reach people, `TableRefusal` to say
      no, `RunHousekeepingAsync` for its clock); `Cards.Server` is only the SignalR shell.
      Tested running with no web server at all, as a phone host will.
- [x] **Server as an API** — `Tables:ServeWebClient` false serves no web app, and
      `Tables:AllowedOrigins` opens the hub to a front end hosted elsewhere; the web app
      finds the server by `TableServer`. `api` / `separate` launch profiles for running
      the two apart in development.
- [ ] **Backlog: phone app on shared tables** — as a player: `TableConnection` lives in
      `Cards.App` and the SignalR client runs on MAUI; the MAUI pages need the lobby and
      the view-driven table. As a host: the app runs `Cards.Hosting`'s `RoomService` with
      its own `ITableClients`. The likeliest transport is ASP.NET Core's Kestrel and the
      same SignalR hub embedded in the app, so web and phone players join a phone host
      exactly as they join the server (works on Android and Windows; iOS only hosts while
      the app is in the foreground and asks for local-network permission). The host's own
      player talks to `RoomService` in process. The old peer-hosted
      `GameServer`/`GameClient` over `TcpTransport` should then be retired.
- [x] **Shared games survive a restart** — each room (seats, tokens, votes, the game,
      and how each seat reads the lines on show) is written to an `IRoomStore` within a
      second of changing and on shutdown, and read back before the server takes
      connections; people rejoin by the token they hold. `FileRoomStore` in
      `Cards.Hosting`, so a phone host can keep its tables too. On the home server,
      systemd's `StateDirectory=cards` gives it `/var/lib/cards` — the unit file must be
      copied once more (deploy/README.md). The web client retries for five minutes.
- [x] **Small-screen layouts** — a table taller than wide (a phone held upright) seats
      every opponent across the top in cells, one row each or two or three to a row,
      whichever gives their cards the most room; another team's melds and the dealer get
      full-width rows; tricks keep a compass round the middle. A zone may give a
      `layout.portrait` region or place of its own: Hand and Foot's meld strip runs the
      full width in two rows of slots, Pinochle's melds, Golf's drawn card, Blackjack's
      split and Poker's community cards each have one. The layout test checks every game
      upright; `TABLE_SHEET=1` renders phone tables too. Phones still double the small
      labels (`LabelScale`).
- [x] **Deal for the first dealer** — `rounds.first_dealer` is `random`, `high_card`,
      `low_card`, or `{ "deal_until": { card } }`: a fresh shuffle dealt face up round the
      table from the first seat until the card turns up (ties redeal for high and low).
      Every card is in the log and the dealer says so at the table. Euchre deals to the
      first jack, Poker to the first ace.
- [x] **Zone captions on every zone** — a declared `label` was drawn only on hands, so
      Hand and Foot's DECK and DISCARD never showed; now every zone's does. Hand and Foot's
      meld strip declared an `{owner}` caption that had never shown and landed on the hand
      once it did; it is gone, the seat's name already says whose melds they are.
- [x] **Show the deal for the deal** — before the shuffle, the cards dealt to choose the
      dealer fly out face up one seat at a time to the one that decides it, lie a moment,
      and are gathered back (`GameState.FirstDealerDraw`, never saved or sent; played by
      `RendererTableAnimator`). Single-player tables only: a shared table's players see
      the log line and the dealer's bubble.
- [x] **Bot names** — computer seats draw from a pool of short names (`BotNames`),
      never repeating at a table and never a seated person's name; the host picks them
      (`GameState.BotNames`), so the engine's own seeded runs stay "Bot 1", "Bot 2". Saves
      keep everyone's names, so a resumed game's log still matches its table. Plain
      names for now — bot-flavoured ones ("Robo Rita", "Deal-E") would be a list change.
- [ ] **Backlog: sign in with Google and Facebook** — deferred until the server has a public
      domain: providers only redirect to registered HTTPS URLs (localhost aside), and
      cards.local is neither. Each needs an app registration (Google OAuth client;
      Facebook app with a privacy policy and data-deletion page), the secrets kept on the
      server (`EnvironmentFile=`), and the server doing the sign-in so no client holds a
      key — cookie for the web app, `WebAuthenticator` and a server-issued token for the
      phone app. Guests stay as they are. Unlocks bans that stick, seats that follow a
      person between devices, and reopening a table with the same people. An iOS app
      offering it must offer Sign in with Apple too.
- [ ] **Backlog: kicking and banning** — the host removes someone from the table: in the
      lobby the seat opens again; during play it goes to the computer, as when someone
      leaves, and the seat's token is revoked so they cannot rejoin it. Banning keeps them
      out of that room for good. Worth deciding when picked up: whether kicking during
      play needs the table's agreement (the dropped-player vote is the pattern), and what
      a ban holds on to — a device id remembered by the client stops a casual return but
      not a determined one, since there are no accounts.
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
- [x] Sound effects in the web client — shuffle, deal, card played, drawn, turned,
      trick gathered, points scored, a bell on your turn, win and lose. Synthesised
      (`SoundGenerator`), heard from what each move changed (`TableSounds`), played through
      Web Audio (`js/sounds.js`); on/off and volume in Settings. The MAUI app still plays
      its original four. Real recorded samples would be a swap of the generator's output
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
      - ~~`dealer: "random"` and the descriptive `order` lines~~ — deleted: they
        duplicated `rounds.first_dealer` and described play the engine does by rule.
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
- [x] **Score card: total, detail, folded, and a sheet on phones** — a tap switches
      totals and rounds; the fold mark folds it to a trophy badge, and a tap on the
      badge opens it again; the fold is remembered from one game to the next. On a phone
      it is not drawn at all: the trophy in the top bar opens the scores as a sheet in
      readable type. One reading of the scores (`ScoreSheet`) feeds both. Every game with
      scores gets the phone sheet, by team where there are teams, even without a
      `score_card` of its own.
- [x] **Your name at your own table** — seat 0 of a single-player game takes the name
      saved in settings (the same one a shared table asks for), fresh or resumed; the
      computer seats stay "Bot N".
- [x] **Tidy build** — no compiler or analyzer warnings across the solution; the
      cache timing test judges each side on its best of seven rounds, so a busy full run
      no longer fails it.
- [ ] **Collapsible table elements — detail and total** — the score card is done (above);
      anything else placed on the table that can grow (meld spreads, badges) should
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
