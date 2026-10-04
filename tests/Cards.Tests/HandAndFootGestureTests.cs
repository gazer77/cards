using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The double tap in Hand and Foot: on the pile it claims the pile — not the deck, whose
/// five decks hold twins of the pile's top card — and on a card in hand it finishes what
/// the taps before it started: onto a meld, down as a meld, or into the discard.
/// </summary>
public sealed class HandAndFootGestureTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static int _uid = 8000;

    private static List<Card> Make(params (Rank Rank, Suit Suit)[] cards)
        => cards.Select(c => new Card(c.Suit, c.Rank, isFaceUp: true) { Uid = _uid++ }).ToList();

    private static List<Card> Give(Zone zone, params (Rank Rank, Suit Suit)[] cards)
    {
        var added = Make(cards);
        foreach (var card in added) zone.Add(card);
        return added;
    }

    private static (GameState State, IGameLogic Logic) Discarding()
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(5) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);

        foreach (var zone in state.Zones.Values.Where(z => z.Id.StartsWith("meld")))
            zone.Clear();

        // This side has opened, with kings; the hand is what each test gives it.
        string me = state.CurrentPlayer.Id;
        state.Zones[$"meld:{me}"].AddGroup(Make((Rank.King, Suit.Clubs), (Rank.King, Suit.Hearts), (Rank.King, Suit.Spades)));
        state.Zones[$"hand:{me}"].Clear();
        state.Metadata["dd_turn_state"] = "discard";
        return (state, logic);
    }

    private static Zone Hand(GameState s) => s.Zones[$"hand:{s.CurrentPlayer.Id}"];

    [Fact]
    public void A_double_tap_on_a_card_that_fits_a_meld_adds_it()
    {
        var (state, logic) = Discarding();
        var king = Give(Hand(state), (Rank.King, Suit.Diamonds), (Rank.Four, Suit.Clubs))[0];

        var shortcut = logic.GetDefaultCardAction(state, king.Id, king.Uid);
        Assert.Equal("add_to_meld", shortcut?.Type);
        Assert.False(state.Metadata.ContainsKey("selected_card"));   // asking left no mark

        logic.Apply(state, shortcut!);
        Assert.DoesNotContain(king, Hand(state).Cards);
        Assert.Contains(king, state.Zones[$"meld:{state.CurrentPlayer.Id}"].Cards);
    }

    [Fact]
    public void A_double_tap_on_the_third_of_a_picked_set_lays_it()
    {
        var (state, logic) = Discarding();
        var nines = Give(Hand(state), (Rank.Nine, Suit.Clubs), (Rank.Nine, Suit.Hearts), (Rank.Nine, Suit.Spades), (Rank.Four, Suit.Clubs));

        // Two picked; the double tap is on the third. Its first tap has already picked it
        // or not — the shortcut counts it in either way.
        state.Metadata["selected_card"] = $"{nines[0].Uid},{nines[1].Uid}";
        var shortcut = logic.GetDefaultCardAction(state, nines[2].Id, nines[2].Uid);
        Assert.Equal("meld", shortcut?.Type);

        logic.Apply(state, shortcut!);
        Assert.Single(Hand(state).Cards);
        Assert.All(nines.Take(3), n => Assert.Contains(n, state.Zones[$"meld:{state.CurrentPlayer.Id}"].Cards));
    }

    [Fact]
    public void A_double_tap_on_wilds_alone_neither_guesses_a_meld_nor_throws_one_away()
    {
        // The report: three picked wilds went onto the kings — a meld the shortcut chose.
        var (state, logic) = Discarding();
        var wilds = Give(Hand(state), (Rank.Two, Suit.Clubs), (Rank.Two, Suit.Hearts), (Rank.Two, Suit.Spades),
                                      (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts));

        state.Metadata["selected_card"] = $"{wilds[0].Uid},{wilds[1].Uid}";
        Assert.Null(logic.GetDefaultCardAction(state, wilds[2].Id, wilds[2].Uid));

        state.Metadata.Remove("selected_card");
        Assert.Null(logic.GetDefaultCardAction(state, wilds[0].Id, wilds[0].Uid));

        // With a natural picked, the wild goes where the natural names.
        var king = Give(Hand(state), (Rank.King, Suit.Diamonds))[0];
        state.Metadata["selected_card"] = $"{king.Uid}";
        Assert.Equal("add_to_meld", logic.GetDefaultCardAction(state, wilds[0].Id, wilds[0].Uid)?.Type);
    }

    [Fact]
    public void A_double_tap_on_a_lone_card_that_melds_nowhere_discards_it()
    {
        var (state, logic) = Discarding();
        var four = Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts))[0];

        var shortcut = logic.GetDefaultCardAction(state, four.Id, four.Uid);
        Assert.Equal("discard", shortcut?.Type);

        logic.Apply(state, shortcut!);
        Assert.Equal(four.Uid, state.Zones["discard"].TopCard!.Uid);
    }

    [Fact]
    public void A_double_tap_on_one_of_several_picked_cards_that_make_nothing_does_nothing()
    {
        var (state, logic) = Discarding();
        var cards = Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.Seven, Suit.Hearts));
        state.Metadata["selected_card"] = $"{cards[0].Uid},{cards[1].Uid}";

        // Not a meld, and two cards are not a discard: no guessing which was meant.
        Assert.Null(logic.GetDefaultCardAction(state, cards[1].Id, cards[1].Uid));
    }

    [Fact]
    public async Task A_double_tap_on_the_pile_claims_the_pile_though_the_deck_holds_its_twin()
    {
        var vm = new Cards.App.GameTableViewModel(
            new GameLoader(new EmbeddedGameAssetSource()),
            new Cards.Services.GameSaveService(new NoSaveStore()));
        vm.TurnPace = 0;
        vm.MinimumTurnPause = TimeSpan.Zero;
        await vm.StartAsync("hand-and-foot", 2, resume: false, seed: 8);

        var state = vm.State!;
        string me = state.CurrentPlayer.Id;
        Assert.False(state.PlayerAgents.ContainsKey(me));   // the person draws first

        // Opened with kings; two nines in hand; a nine on the pile — and its twin in the deck.
        foreach (var zone in state.Zones.Values.Where(z => z.Id.StartsWith("meld"))) zone.Clear();
        state.Zones[$"meld:{me}"].AddGroup(Make((Rank.King, Suit.Clubs), (Rank.King, Suit.Hearts), (Rank.King, Suit.Spades)));
        Give(state.Zones[$"hand:{me}"], (Rank.Nine, Suit.Clubs), (Rank.Nine, Suit.Hearts));
        var top = Give(state.Zones["discard"], (Rank.Nine, Suit.Spades))[0];
        Give(state.Zones["deck"], (Rank.Nine, Suit.Spades));
        Assert.Contains(vm.Logic!.GetValidActions(state), a => a.Type == "draw_from_discard");

        int deckBefore = state.Zones["deck"].Count;
        await vm.ActivateCard(top.Id, top.Uid);

        Assert.Equal(deckBefore, state.Zones["deck"].Count);
        Assert.Contains(top, state.Zones[$"hand:{me}"].Cards.Concat(state.Zones[$"meld:{me}"].Cards));
    }

    [Fact]
    public void A_double_tap_on_the_decks_top_card_leaves_its_twin_in_hand_alone()
    {
        // The report: after drawing, a double tap on the deck discarded a king from hand.
        // The deck's top card, under its back, was that king's twin.
        var (state, logic) = Discarding();
        var king = Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.King, Suit.Diamonds))[1];
        state.Zones[$"meld:{state.CurrentPlayer.Id}"].Clear();   // nowhere for the king to go either
        var twin = Give(state.Zones["deck"], (Rank.King, Suit.Diamonds))[0];
        twin.IsFaceUp = false;

        Assert.Null(logic.GetDefaultCardAction(state, twin.Id, twin.Uid));
        Assert.Equal("discard", logic.GetDefaultCardAction(state, king.Id, king.Uid)?.Type);
    }

    [Fact]
    public async Task A_double_tap_on_the_deck_after_drawing_does_nothing()
    {
        var vm = new Cards.App.GameTableViewModel(
            new GameLoader(new EmbeddedGameAssetSource()),
            new Cards.Services.GameSaveService(new NoSaveStore()));
        vm.TurnPace = 0;
        vm.MinimumTurnPause = TimeSpan.Zero;
        await vm.StartAsync("hand-and-foot", 2, resume: false, seed: 8);

        var state = vm.State!;
        string me = state.CurrentPlayer.Id;
        await vm.Invoke(new GameAction("draw_from_deck"));
        Assert.Equal("discard", state.Metadata["dd_turn_state"]);

        // Every card in hand has a twin on top of the deck, one after another.
        var hand = state.Zones[$"hand:{me}"];
        int before = hand.Count;
        foreach (var card in hand.Cards.ToList())
        {
            var twin = Give(state.Zones["deck"], (card.Rank, card.Suit))[0];
            twin.IsFaceUp = false;
            await vm.ActivateCard(twin.Id, twin.Uid);
            Assert.Equal(before, hand.Count);
            Assert.Equal(me, state.CurrentPlayer.Id);
        }
    }

    [Fact]
    public void A_shared_table_offers_the_shortcut_for_the_card_in_hand_not_its_twin_on_the_pile()
    {
        var (state, logic) = Discarding();
        string me = state.CurrentPlayer.Id;
        state.Zones[$"meld:{me}"].Clear();
        var king = Give(Hand(state), (Rank.Four, Suit.Clubs), (Rank.King, Suit.Diamonds))[1];
        var twin = Give(state.Zones["discard"], (Rank.King, Suit.Diamonds))[0];

        // The pile's king comes first in the table's zones; the shortcut is still the hand's.
        var view = Cards.Engine.Shared.TableProjection.For(state, logic, me, 2, [],
            state.Players.Select(p => new Cards.Engine.Shared.SeatView { Id = p.Id, Name = p.Name }).ToList(),
            uid => -1000 - uid * 7 % 991, version: 1, busy: false, announcements: []);
        Assert.Equal(king.Uid, view.DefaultCardActions[king.Id].CardUid);

        var remote = new Cards.Engine.Shared.RemoteGameLogic(view, _ => Task.CompletedTask);
        Assert.Equal("discard", remote.GetDefaultCardAction(state, king.Id, king.Uid)?.Type);
        Assert.Null(remote.GetDefaultCardAction(state, twin.Id, twin.Uid));
    }

    [Fact]
    public void The_score_card_is_by_team_at_four_and_by_player_at_three()
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hand-and-foot")!;
        Assert.Equal("team", definition.ScoreCard?.By);

        foreach (var (seats, rows) in new[] { (4, 2), (3, 3) })
        {
            var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(5) };
            LogicRegistry.Create(definition).Initialize(state, seats, []);
            Assert.Equal(rows, ScoreSheet.For(state)!.Rows.Count);
        }
    }
}
