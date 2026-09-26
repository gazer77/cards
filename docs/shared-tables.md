# Shared tables

Several people at one game, each on their own screen: the web app today, the phone app
through the same server when it is wired up.

## How it works

The game runs in one place — the table server (`src/Cards.Server`). Clients never run the
rules and never hold another player's cards.

```
 browser / phone ──SignalR──►  TableHub ──►  RoomService ──► the game (Cards.Core)
        ▲                                         │
        └──────────── TableView, one per seat ◄───┘
```

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

## Running the server

The server also serves the web app, so it is the only thing to run.

```
dotnet run --project src/Cards.Server --launch-profile http
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

Running the web app on its own (`dotnet run --project src/Cards.Web`) still works for
single-player. To use shared tables from it, point it at a running server with
`"TableServer": "http://localhost:5280"` in `src/Cards.Web/wwwroot/appsettings.json`
(the server then needs to allow that origin — simplest is to use the server's own
address instead).

## Playing

1. Pick a game, choose seats and rules, press **Play with friends**. You get a five-letter
   table code and a link.
2. Friends enter the code on the home page (or open the link) and type their name.
3. The host presses **Deal**. Seats nobody took are played by the computer.
4. Leaving mid-game hands your seat to the computer so the others can finish.

Rooms close after six hours with nobody touching them, and all rooms end when the server
restarts — games live in memory.

## Not yet

- **Go Fish** is written as one person against the computer and is refused at the door
  (`IPhaseHandler.SharedTableReady`). It needs its turn logic made symmetric.
- **A player who drops mid-turn** holds the table until they come back or leave; there is
  no timeout that hands the seat to the computer.
- **The phone app** is not wired to the server yet. `TableConnection` (in `Cards.App`) is
  the client both should use.
- **Saving a shared game** — rooms live in memory. See plan.md.
- **Computer difficulty** — computer seats all play the default strategy.
- Two tabs in one browser share the remembered seat, so testing two players on one
  machine wants two browsers (or a private window).
