# Cards — Mobile Card Game Platform

A mobile app containing many different card games, all in one place.
If a game is played with cards, this app should have it.

---

## Tech Stack

- **Framework:** .NET MAUI (C#) — Android-first, iOS-ready when a Mac is available
- **Game Table Rendering:** SkiaSharp (2D canvas inside MAUI) or embedded MonoGame canvas
- **App UI (menus, settings, rules):** MAUI widgets
- **Multiplayer Backend:** Lightweight server (SignalR over ASP.NET Core) — friends-only via room codes, no matchmaking
- **Local/Offline Multiplayer:** Bluetooth or Wi-Fi Direct (Android Nearby Connections API)

---

## Games

### Poker
- Texas Hold'em
- Stud
- Wilds
- Trips or Better
- Follow the Queens / Kings
- Low Hole
- Blind Baseball

### Euchre
- 4-player
- 3-player
- 2-player

### Other Games
- Hand and Foot
- Pinochle
- War
- Blackjack
- Gin Rummy
- Go Fish
- Hearts
- Spades
- Golf

---

## Features

### Multiplayer
- **Internet:** Friends-only via shareable room codes (no matchmaking or strangers)
  - *Stretch goal (maybe someday):* public matchmaking with a full backend
- **Local Network (LAN):** Device discovery via mDNS
- **Local Offline:** Bluetooth / Wi-Fi Direct (no internet required)

### AI Opponents
- **Easy** — erratic, makes frequent mistakes
- **Normal** — plays valid moves, low chance of error
- **Hard** — optimal valid play, no mistakes, no strategy
- **Insane** — no mistakes and uses game-specific strategy

### Customization
- Multiple deck styles
- Multiple table backgrounds
- Player-uploaded custom backgrounds
- Custom house rules (per-game toggles)

### Learning & Rules
- Rules reference section for every game
- Learn-to-play mode for every game

---

## Task List

### Phase 1 — Foundation
- [ ] Set up .NET MAUI project (Android target)
- [ ] Integrate SkiaSharp canvas for game table rendering
- [ ] Build core card engine (deck, hand, deal, shuffle, standard + pinochle decks)
- [ ] Build card rendering system (face, back, flip animation, drag-and-drop)
- [ ] Design and build app shell (home screen, game picker, settings screen)
- [ ] Implement deck style asset system (swappable card face/back skins)
- [ ] Implement table background system + custom image picker
- [ ] Build rules/learn-to-play content framework (scrollable pages per game)

### Phase 2 — First Games (Simple)
- [ ] War
- [ ] Go Fish
- [ ] Blackjack (player vs dealer)
- [ ] Hearts
- [ ] Spades
- [ ] Gin Rummy

### Phase 3 — AI System
- [ ] Design AI player interface/framework (plug-in per game)
- [ ] Easy AI (random valid moves, frequent errors)
- [ ] Normal AI (best valid move, low error rate)
- [ ] Hard AI (optimal play, no errors, no strategy)
- [ ] Insane AI (per-game strategy engines — implemented per game in Phase 4+)

### Phase 4 — Complex Games
- [ ] Poker hand evaluator (shared engine for all poker variants)
- [ ] Texas Hold'em
- [ ] Remaining poker variants (stud, wilds, trips or better, follow the queen/king, low hole, blind baseball)
- [ ] Euchre 4-player
- [ ] Euchre 3-player
- [ ] Euchre 2-player
- [ ] Pinochle (48-card deck, meld scoring, bidding)
- [ ] Hand and Foot
- [ ] Golf

### Phase 5 — Multiplayer
- [ ] Build SignalR game server (ASP.NET Core) — room code creation and joining
- [ ] Internet multiplayer: create/join room by code, game state sync, reconnect handling
- [ ] LAN multiplayer: mDNS device discovery, local game hosting
- [ ] Offline local multiplayer: Bluetooth / Wi-Fi Direct (Android Nearby Connections)
- [ ] Player profiles (name, avatar) — local only to start

### Phase 6 — Polish & Release
- [ ] Custom house rules (per-game rule toggles)
- [ ] Sound effects and card animations polish
- [ ] Accessibility (colorblind mode, font size options)
- [ ] Android release prep (Play Store listing, signing, testing)
- [ ] *Future:* iOS build when Mac access is available

---

## Feasibility Notes

| Feature | Feasibility | Notes |
|---|---|---|
| War, Go Fish, Blackjack, Hearts, Spades, Gin Rummy | High | Straightforward rules and AI |
| Poker variants | Medium-High | Hand evaluator is reusable; edge cases need care |
| Euchre variants | Medium | 3-player variant has unusual rules |
| Pinochle | Medium | Custom deck, complex meld/bidding logic |
| Hand and Foot | Medium | Long game, complex meld rules |
| Golf | Medium | Many regional variants — pick one rule set |
| Internet multiplayer (friends/room codes) | Medium | Manageable with SignalR; needs hosted server |
| LAN multiplayer | Medium | mDNS discovery is well-supported on Android |
| Offline local (Bluetooth) | Medium | Android Nearby Connections API handles this |
| AI Easy/Normal/Hard | High | Standard game tree approaches work well |
| AI Insane (strategy) | Low-Medium | Per-game research needed (e.g., poker GTO) |
| Deck styles / backgrounds | High | Asset swapping is straightforward |
| Custom house rules | Low-Medium | Needs flexible per-game rule toggle system |
| Rules / learn-to-play content | High | Content work, not engineering complexity |
