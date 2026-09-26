using Cards.Engine;
using Cards.Engine.Shared;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// One game, several people: each seat sees the table from its own chair, reads the
/// table's words as addressed to itself, sees only the cards it could see across a real
/// table, and may do only what its own view offered.
/// </summary>
public sealed class SharedTableTests
{
    private static (GameState State, IGameLogic Logic) Table(string gameId, int seats, ulong seed = 3)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(gameId).GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static List<SeatView> Seats(GameState state)
        => state.Players.Select(p => new SeatView { Id = p.Id, Name = p.Name }).ToList();

    private static TableView View(GameState state, IGameLogic logic, string viewer)
        => TableProjection.For(state, logic, viewer, state.Players.Count(p => p.Role is null), [],
            Seats(state), uid => -1000 - uid * 7 % 991, version: 1, busy: false, announcements: []);

    // ── Words ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_line_addressed_to_a_seat_reads_as_you_to_that_seat_only()
    {
        var (state, _) = Table("hearts", 4);
        state.Players[1].Name = "Ana";

        string stored = GameText.Message(state, "turn_draw", "{player}'s turn — Draw a card", "player1");

        Assert.Equal("Ana's turn — Draw a card", stored);                       // seat 0 reads it
        Assert.Equal("Your turn — Draw a card", GameText.Render(state, stored, "player1"));
        Assert.Equal("Ana's turn — Draw a card", GameText.Render(state, stored, "player2"));
    }

    [Fact]
    public void Seat_zero_reads_what_it_always_read()
    {
        var (state, _) = Table("hearts", 4);

        string stored = GameText.Message(state, "turn_draw", "{player}'s turn — Draw a card", "player0");

        Assert.Equal("Your turn — Draw a card", stored);
        Assert.Equal($"{state.Players[0].Name}'s turn — Draw a card", GameText.Render(state, stored, "player3"));
    }

    [Fact]
    public void A_summary_names_each_reader_as_you()
    {
        var (state, _) = Table("hearts", 3);
        state.Players[0].Name = "Bo";
        state.Players[1].Name = "Ana";
        state.Players[2].Name = "Cy";

        string stored = GameText.PerViewer(state, viewer =>
            string.Join(" | ", state.Players.Select(p => p.Id == viewer ? "You" : p.Name)));

        Assert.Equal("You | Ana | Cy", stored);
        Assert.Equal("Bo | You | Cy", GameText.Render(state, stored, "player1"));
        Assert.Equal("Bo | Ana | Cy", GameText.Render(state, stored, null));
    }

    // ── Cards ─────────────────────────────────────────────────────────────────

    [Fact]
    public void A_seat_sees_its_own_hand_and_only_backs_of_the_others()
    {
        var (state, logic) = Table("hearts", 4);
        var view = View(state, logic, "player1");

        var mine = view.State.Zones.Single(z => z.Id == "hand:player1");
        Assert.All(mine.Cards, c => Assert.False(c.IsHidden));
        Assert.Equal(state.Zones["hand:player1"].Cards.Select(c => c.Uid), mine.Cards.Select(c => c.Uid));

        foreach (var other in new[] { "hand:player0", "hand:player2", "hand:player3" })
        {
            var theirs = view.State.Zones.Single(z => z.Id == other);
            Assert.Equal(state.Zones[other].Count, theirs.Cards.Count);   // how many, yes
            Assert.All(theirs.Cards, c => Assert.True(c.IsHidden));        // which, no

            // Not even the uid gives it away: a uid names a card from the deal on.
            var real = state.Zones[other].Cards.Select(c => c.Uid).ToHashSet();
            Assert.DoesNotContain(theirs.Cards, c => real.Contains(c.Uid));
        }
    }

    [Fact]
    public void A_face_down_card_on_an_open_zone_stays_hidden()
    {
        var (state, logic) = Table("golf", 2);
        var grid = state.Zones.Values.First(z => z.Id.StartsWith("grid:"));
        Assert.Contains(grid.Cards, c => !c.IsFaceUp);

        var view  = View(state, logic, grid.OwnerId!);
        var shown = view.State.Zones.Single(z => z.Id == grid.Id).Cards;

        for (int i = 0; i < grid.Cards.Count; i++)
            Assert.Equal(!grid.Cards[i].IsFaceUp, shown[i].IsHidden);   // even to its owner
    }

    [Fact]
    public void A_card_drawn_off_the_deck_is_named_only_to_the_player_who_drew_it()
    {
        var (state, logic) = Table("golf", 2);
        // Past the peeks, to an ordinary turn — the computer taking every seat to get there.
        state.PlayerAgents["player0"] = new SmartDefaultAiAgent("player0", state.Rng);
        for (int i = 0; i < 50 && logic.GetValidActions(state).All(a => a.Type != "draw_from_deck"); i++)
            logic.Apply(state, logic.GetAutoAction(state));
        string drawer = state.CurrentPlayer.Id;
        string other  = state.Players.First(p => p.Id != drawer).Id;
        state.PlayerAgents.Remove(drawer);

        logic.Apply(state, new GameAction("draw_from_deck"));
        Assert.True(state.Metadata.ContainsKey("dd_drawn_card"));

        Assert.True(View(state, logic, drawer).State.Metadata.ContainsKey("dd_drawn_card"));
        Assert.False(View(state, logic, other).State.Metadata.ContainsKey("dd_drawn_card"));
    }

    [Fact]
    public void Nobody_sees_into_the_deck()
    {
        var (state, logic) = Table("hearts", 4);
        state.Zones.TryGetValue("deck", out var deck);
        if (deck is null || deck.IsEmpty) return;   // hearts deals the whole pack

        var view = View(state, logic, "player0");
        Assert.All(view.State.Zones.Single(z => z.Id == "deck").Cards, c => Assert.True(c.IsHidden));
    }

    [Fact]
    public void A_client_rebuilds_the_table_from_its_view()
    {
        var (state, logic) = Table("hearts", 4);
        state.Players[2].Name = "Cy";
        var view = View(state, logic, "player2");

        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var rebuilt = TableProjection.ToState(view, loader.LoadAsync("hearts").GetAwaiter().GetResult()!);

        Assert.Equal("player2", rebuilt.Viewer);
        Assert.Equal("Cy", rebuilt.Players[2].Name);
        foreach (var (id, zone) in state.Zones)
            Assert.Equal(zone.Count, rebuilt.Zones[id].Count);
        Assert.Equal(state.Zones["hand:player2"].Cards.Select(c => c.Id), rebuilt.Zones["hand:player2"].Cards.Select(c => c.Id));
        Assert.All(rebuilt.Zones["hand:player0"].Cards, c => Assert.StartsWith("hidden", c.Id));
    }

    // ── Who may act ───────────────────────────────────────────────────────────

    [Fact]
    public void Only_the_seat_to_play_is_offered_moves()
    {
        var (state, logic) = Table("hearts", 4);
        // Past the pass, so there is a single seat to act.
        state.Metadata.Remove("pass_direction");
        string current = state.CurrentPlayer.Id;
        string other   = state.Players.First(p => p.Id != current).Id;

        var theirs = View(state, logic, other);
        Assert.Empty(theirs.SelectableCardIds);
        Assert.DoesNotContain(theirs.Actions, a => !SeatGate.IsTableGesture(a));
    }

    [Fact]
    public void A_move_out_of_turn_is_refused()
    {
        var (state, logic) = Table("blackjack", 2);
        string current = state.CurrentPlayer.Id;
        string other   = state.Players.First(p => p.Id != current && p.Role is null).Id;
        state.PlayerAgents.Remove(other);   // a person sits there, as at a shared table

        Assert.True(SeatGate.Allows(state, logic, current, new GameAction("stand"), out _));
        Assert.False(SeatGate.Allows(state, logic, other, new GameAction("stand"), out var why));
        Assert.Contains("not your turn", why);
    }

    [Fact]
    public void A_card_not_on_offer_is_refused()
    {
        var (state, logic) = Table("hearts", 4);
        string current = state.CurrentPlayer.Id;
        var foreign = state.Zones.Values
            .Where(z => z.OwnerId is not null && z.OwnerId != current)
            .SelectMany(z => z.Cards)
            .First(c => !state.Zones[$"hand:{current}"].Cards.Any(m => m.Id == c.Id));

        Assert.False(SeatGate.Allows(state, logic, current, new GameAction("select_card", CardId: foreign.Id), out _));

        var own = logic.GetSelectableCardIds(state).First();
        Assert.True(SeatGate.Allows(state, logic, current, new GameAction("select_card", CardId: own), out _));
    }

    [Fact]
    public void A_computer_seat_cannot_be_played_from_outside()
    {
        var (state, logic) = Table("blackjack", 2);
        string current = state.CurrentPlayer.Id;
        state.PlayerAgents[current] = new SmartDefaultAiAgent(current, state.Rng);

        Assert.False(SeatGate.Allows(state, logic, current, new GameAction("stand"), out _));
    }

    [Fact]
    public void Every_shipped_game_can_seat_several_people()
    {
        // Go Fish was the last one written as one person against the computer.
        foreach (var id in new[] { "go-fish", "hearts", "blackjack", "golf", "hand-and-foot", "poker", "euchre" })
            Assert.True(Table(id, id == "euchre" ? 4 : 2).Logic.SharedTableReady, id);
    }

    // ── The chair ─────────────────────────────────────────────────────────────

    [Fact]
    public void The_viewer_sits_at_the_bottom_and_sees_their_own_hand()
    {
        var (state, _) = Table("hearts", 4);
        var canvas = new SKImageInfo(1000, 800);

        state.ViewerId = "player2";
        var layouts = ZoneLayoutEngine.Compute(state, canvas);
        var mine    = layouts.Single(l => l.Zone.Id == "hand:player2");
        var seat0   = layouts.Single(l => l.Zone.Id == "hand:player0");

        Assert.True(mine.Bounds.MidY > canvas.Height * 0.75f);   // bottom edge
        Assert.True(seat0.Bounds.MidY < canvas.Height * 0.25f);  // across the table
        Assert.True(mine.FaceUp);
        Assert.False(seat0.FaceUp);
    }
}
