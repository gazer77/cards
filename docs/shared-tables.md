# Shared tables

Several people at one game, each on their own screen: the web app today, the phone app
through the same server when it is wired up.

## How it works

The game runs in one place — the host. Today that is the table server
(`src/Cards.Server`); later it can be a phone. Clients never run the rules and never hold
another player's cards.

```
 browser / phone ──SignalR──►  TableHub ──►  RoomService ──► the game (Cards.Core)
        ▲                     (Cards.Server)  (Cards.Hosting)       │
        └──────────── TableView, one per seat ◄─────────────────────┘
```

The hosting is split in two so a phone can host with the same code:

| Project | What it is | Depends on |
|---|---|---|
| `Cards.Hosting` | Rooms, seats, the rules vote, dropped-player votes, the game loop, who sees what. Refuses with `TableRefusal`; reaches people through `ITableClients`; its clock is `RunHousekeepingAsync`. | `Cards.Core` only — no web framework |
| `Cards.Server` | The table server: the SignalR hub, `SignalRTableClients`, a filter turning refusals into hub errors, a background service running the clock, and (optionally) the web app. | ASP.NET Core, `Cards.Hosting` |

A phone hosting a game references `Cards.Hosting`, supplies its own `ITableClients` for
however the other players are connected, and runs `RunHousekeepingAsync` while it hosts.
Its own player can call `RoomService` directly, with no network in between.

- A client sends what its player wants to do (`Act`). The server checks the seat may do it
  (`SeatGate`: its turn, a card on offer, a zone that card may go to), applies it, and
  sends every seat its own `TableView`.
- A `TableView` is the save format with every card the seat could not see across a real
  table replaced by a back under an alias (`TableProjection`). Aliases are keyed per room,
  so not even a card's uid — which follows it from the deal — leaks. The table's words are
  in the seat's own reading: "Your turn" to you, "Ana's turn" to everyone else
  (`GameText.Render`).
- The client turns the view back into an ordinary table (`TableProjection.ToState`) and
  draws it with the same renderer, view model and gestures as a local game. The table
  turns so the viewer always sits at the bottom (`GameState.ViewerId`).
- Computer players, the dealer and every other automatic step run on the server at the
  engine's own pace; people wait while they do (`TableView.IsBusy`).
- A seat belongs to a token the device remembers, not to a connection. A phone that
  sleeps or a page that reloads rejoins the same seat.

The hub is SignalR because the same client library runs in the browser (Blazor
WebAssembly) and in MAUI on Android, iOS and Windows, so phone and web players meet at
one server. It reconnects by itself and falls back from WebSockets when it must.

## Running from Visual Studio

Open `src/Cards.slnx`.

- **Server and web app together** — right-click **Cards.Server** → *Set as Startup
  Project*, pick the **http** profile beside the Start button, and press F5. The server
  starts and the browser opens on `http://localhost:5280` with the web app in it.
  Breakpoints work in both the server and the web app's own code.
- **The same, reachable from phones on your network** — the **lan** profile. It listens on
  every interface, so no browser opens; browse to `http://<this machine's address>:5280`.
- **API and web app as two projects** — pick **Server + web app, separately** from the
  Start button's list (it comes from `src/Cards.slnLaunch`). The server starts as an API
  on :5280 and the web app on :5277 opens in the browser, pointed at it. If that entry
  is not listed, make it once under *Configure Startup Projects…* → *Multiple startup
  projects*: Cards.Server with the **api** profile, Cards.Web with **separate**.

## Running the server

The server also serves the web app, so it is the only thing to run.

```
dotnet run --project src/Cards.Server --launch-profile lan
```

It listens on port **5280** on every network interface. Open `http://localhost:5280` on
the machine itself, or `http://<that machine's LAN address>:5280` from any other device on
the network. Allow the port through the firewall the first time (Windows asks).

For a machine that stays up:

```
dotnet publish src/Cards.Server -c Release -o publish/server
publish/server/Cards.Server.exe --urls http://*:5280
```

Players outside your network need a way in: forward a port on the router to this machine,
or run a tunnel (Cloudflare Tunnel, Tailscale Funnel) in front of port 5280. Put HTTPS in
front of it for anything beyond friends and family — a tunnel does that for you.

### As an API behind a separately hosted front end

The server can also run as a backend only, with the web app hosted somewhere else — a
static host, a CDN, or the Blazor dev server while developing. Two settings under
`Tables` in `src/Cards.Server/appsettings.json` (or `--Tables:…` on the command line):

| Setting | Default | Meaning |
|---|---|---|
| `ServeWebClient` | `true` | Serve the web app from the server. `false` for API only. |
| `AllowedOrigins` | `[]` | Every address the web app is served from. The browser calls the hub across origins then, and only these are let in. |

The web app finds the server through `TableServer` in its `wwwroot/appsettings.json`; left
empty, it uses the address the page came from.

For development, two terminals:

```
dotnet run --project src/Cards.Server --launch-profile api        # hub on :5280, allows :5277
dotnet run --project src/Cards.Web    --launch-profile separate   # web app on :5277
```

The `separate` profile runs the web app in a `Separate` environment, whose
`appsettings.Separate.json` points it at `http://localhost:5280`. For a real deployment,
set `TableServer` to the server's public address in the published `appsettings.json`, and
list the front end's address in the server's `AllowedOrigins`. Use HTTPS on both once it
leaves your own network.

## Playing

1. Pick a game, choose seats and rules, press **Play with friends**. You get a five-letter
   table code and a link.
2. Friends enter the code on the home page (or open the link) and type their name.
3. The host presses **Deal**. Seats nobody took are played by the computer.
4. Leaving mid-game hands your seat to the computer so the others can finish.

Rooms close after six hours with nobody touching them. A server restart — every deploy is
one — keeps them: each room is written to `Tables:RoomDirectory` (or systemd's
`$STATE_DIRECTORY/rooms`) within a second of changing and once more on shutdown, and read
back before the server takes connections. Everyone reconnects by the seat their device
remembers, as after any dropped connection; the web client keeps retrying for five
minutes. With no folder configured, rooms last only as long as the server runs.

## Someone drops out

A phone that sleeps or a page that reloads rejoins its seat by itself. While someone is
away, the table shows "Waiting for Bo to reconnect…" when it is their turn.

The host chooses, when opening the table, how long a dropped player may hold the table on
their own turn: 30 seconds, 1, 2 or 5 minutes, or never. Past that, everyone still
connected is asked: *let the computer play for them until they're back?*

- A majority saying yes hands the seat to the computer, marked for everyone as standing in.
  The moment the player reconnects, the seat is theirs again, mid-hand.
- Enough saying *wait* closes the question; it is asked again only after another full
  timeout.
- If the player comes back while the question is open, the question simply goes away.

Leaving on purpose (the menu's Leave game) hands the seat to the computer for good.

## Accounts and roles

Every browser gets an account on its first visit — a six-word code in Settings, typed on
another device to carry the name, settings and saved games across. Each account is a
**player**, **manager** or **admin**:

- The first **admin** is made with a one-time setup code the server writes to its log
  (`journalctl -u cards`) while it has none; it is entered in Settings.
- A **manager** opens Settings → *Manage tables*: every open table, with *Take off*,
  *Take off and ban* and *Close table*; the banned, with *Let back*; and which games the
  server offers. A banned account cannot open, join or return to a table here.
- The **admin** sees all that and the people: roles, a new code for someone who lost
  theirs, and deleting an account. The last admin cannot step down; nobody bans an admin,
  and only an admin bans a manager.

## Not yet

- **The phone app** is not wired to the server yet. `TableConnection` (in `Cards.App`) is
  the client both should use.
- **Reopening a finished or closed table later** with the same people — rooms survive a
  restart, but once closed they are gone.
- **Computer difficulty** — computer seats all play the default strategy.
- Two tabs in one browser share the remembered seat, so testing two players on one
  machine wants two browsers (or a private window).
