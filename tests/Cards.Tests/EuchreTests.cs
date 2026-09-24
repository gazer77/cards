using Cards.Engine;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// Euchre's bidding, as a player meets it: whose bid it is, and whether they can see the
/// card they are bidding on.
/// </summary>
public sealed class EuchreTests
{
    private static (GameState State, IGameLogic Logic) Table(string id = "euchre", int seats = 4)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(id).GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    /// <summary>Clockwise is index−1 here, so the dealer's left is the seat before them.</summary>
    private static string LeftOf(GameState state, string playerId)
    {
        int i = state.Players.FindIndex(p => p.Id == playerId);
        return state.Players[(i - 1 + state.Players.Count) % state.Players.Count].Id;
    }

    [Fact]
    public void The_bid_belongs_to_the_right_seat_before_anyone_asks_whose_turn_it_is()
    {
        // The reported bug: a client asks "is this a person's turn?" before it asks
        // "what can they do?", and bidding only decided who was bidding inside the
        // second question. So the table offered Order Up and Pass to whoever the deal
        // happened to leave current — a person bidding in the seat beside them.
        var (state, logic) = Table();
        Assert.Equal("order_up", state.CurrentPhaseId);

        logic.GetAutoAdvanceDelay(state);   // the first question a client asks

        Assert.Equal(LeftOf(state, state.DealerId!), state.CurrentPlayer.Id);
    }

    [Fact]
    public void A_person_is_waited_for_and_an_agent_is_not()
    {
        var (state, logic) = Table();

        // Whoever the bidding names, the answer must match that seat: a delay means the
        // engine will play the turn itself, and it must never do that for a person.
        var delay = logic.GetAutoAdvanceDelay(state);
        bool isAgent = state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id);
        Assert.Equal(isAgent, delay is not null);
    }

    [Fact]
    public void Bidding_starts_left_of_the_dealer_in_every_round()
    {
        var (state, logic) = Table();

        for (int round = 1; round <= 3; round++)
        {
            // Wind on to the next round's bidding, playing whatever is legal.
            while (state.RoundNumber == round && !logic.IsGameOver(state))
            {
                if (state.CurrentPhaseId == "order_up" && state.RoundNumber == round)
                {
                    logic.GetAutoAdvanceDelay(state);   // what a client asks first
                    Assert.Equal(LeftOf(state, state.DealerId!), state.CurrentPlayer.Id);
                    break;
                }
                logic.Apply(state, Step(state, logic));
            }

            while (state.RoundNumber == round && !logic.IsGameOver(state))
                logic.Apply(state, Step(state, logic));

            if (logic.IsGameOver(state)) break;
        }
    }

    private static GameAction Step(GameState state, IGameLogic logic)
    {
        var cards = logic.GetSelectableCardIds(state);
        if (cards.Count > 0 && state.CurrentPhaseId is not ("order_up" or "call_trump"))
            return new GameAction("play_card", CardId: cards[0]);

        var actions = logic.GetValidActions(state);
        return actions.FirstOrDefault(a => a.Type != "bid_alone") ?? new GameAction("tap");
    }

    [Fact]
    public void The_turned_card_is_face_up_for_everyone_at_the_table()
    {
        // It is turned onto the table for the whole table to bid on. The kitty said
        // "top_to_dealer", a word the mask knew and the renderer did not, so the card
        // every player was being asked about was drawn face-down to all of them.
        var (state, _) = Table();

        var kitty = state.Zones["kitty"];
        Assert.True(kitty.TopCard!.IsFaceUp);

        var layout = ZoneLayoutEngine.Compute(state, new SKImageInfo(1400, 900))
            .Single(l => l.Zone.Id == "kitty");
        Assert.True(layout.FaceUp, "The turned card is not drawn face-up.");
    }

    [Fact]
    public void The_rest_of_the_kitty_stays_hidden()
    {
        // Only the top card is turned; the three under it are nobody's business.
        var (state, _) = Table();
        var kitty = state.Zones["kitty"];

        Assert.Equal(4, kitty.Count);
        Assert.Single(kitty.Cards.Where(c => c.IsFaceUp));
        Assert.Same(kitty.TopCard, kitty.Cards.Single(c => c.IsFaceUp));
    }
}

/// <summary>
/// What the table offers a player in a trick, and what the gestures do with it.
/// </summary>
public sealed class TrickInputTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static async Task<Cards.App.GameTableViewModel> Playing(string game, int seats)
    {
        var vm = new Cards.App.GameTableViewModel(
            new GameLoader(new EmbeddedGameAssetSource()),
            new Cards.Services.GameSaveService(new NoSaveStore()));
        vm.TurnPace = 0;
        vm.MinimumTurnPause = TimeSpan.Zero;
        await vm.StartAsync(game, seats, resume: false, seed: 8);

        // Wind on to the person's turn to play a card.
        var state = vm.State!;
        var logic = vm.Logic!;
        for (int i = 0; i < 400; i++)
        {
            if (state.CurrentPhaseId == "play" && vm.SelectableCardIds.Count > 0) break;
            if (logic.GetAutoAdvanceDelay(state) is not null) logic.Apply(state, logic.GetAutoAction(state));
            else
            {
                var actions = logic.GetValidActions(state).Where(a => a.Type != "bid_alone").ToList();
                if (actions.Count > 0) logic.Apply(state, actions[0]);
                else break;
            }
        }
        return vm;
    }

    [Fact]
    public async Task No_button_reads_tap()
    {
        // The reported artifact: a trick phase offers the engine an action with no label,
        // meaning "the table is the affordance". The action bar drew it as a button
        // reading "tap", which did nothing a player could see.
        var vm = await Playing("euchre", 4);

        Assert.Contains(vm.Logic!.GetValidActions(vm.State!), a => a.Type == "tap");
        Assert.DoesNotContain(vm.Actions, a => a.Type == "tap");
        Assert.All(vm.Actions, a => Assert.NotNull(a.Label));
    }

    [Fact]
    public async Task A_double_tap_plays_the_card()
    {
        var vm = await Playing("euchre", 4);
        var state = vm.State!;
        string me = state.CurrentPlayer.Id;

        string cardId = vm.SelectableCardIds[0];
        var card = state.Zones[$"hand:{me}"].Cards.First(c => c.Id == cardId);

        var shortcut = vm.Logic!.GetDefaultCardAction(state, card.Id, card.Uid);
        Assert.NotNull(shortcut);
        Assert.Equal("play_card", shortcut.Type);

        int before = state.Zones[$"hand:{me}"].Count;
        await vm.ActivateCard(card.Id, card.Uid);

        // Played, not merely selected. Where it is afterwards depends on how far the
        // table got — the other seats answer and the trick may already be collected —
        // so what is asserted is that the card left the hand by one gesture.
        Assert.DoesNotContain(state.Zones[$"hand:{me}"].Cards, c => c.Uid == card.Uid);
        Assert.Equal(before - 1, state.Zones[$"hand:{me}"].Count);
    }

    [Fact]
    public async Task A_card_the_rules_refuse_stays_put_however_it_is_tapped()
    {
        // The shortcut is past the asking, never past the rules: following suit still
        // applies, so a card that is not on offer does nothing.
        var vm = await Playing("euchre", 4);
        var state = vm.State!;
        string me = state.CurrentPlayer.Id;

        var offered = vm.SelectableCardIds.ToHashSet();
        var refused = state.Zones[$"hand:{me}"].Cards.FirstOrDefault(c => !offered.Contains(c.Id));
        if (refused is null) return;   // every card was legal this deal; nothing to prove

        await vm.ActivateCard(refused.Id, refused.Uid);
        Assert.Contains(state.Zones[$"hand:{me}"].Cards, c => c.Uid == refused.Uid);
    }
}

/// <summary>
/// A card in your own hand shows you its face.
///
/// "Face-down" is a property of the card; "hidden from the other players" is a
/// property of the zone, and a hand already says <c>visibility: owner</c>. Conflating
/// the two put the card the dealer had just been ordered up into their own hand
/// showing its back — to them.
/// </summary>
public sealed class HandFacingTests
{
    private static (GameState State, IGameLogic Logic) Euchre()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("euchre").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 4, []);
        return (state, logic);
    }

    [Fact]
    public void The_card_ordered_up_joins_the_dealers_hand_face_up()
    {
        var (state, logic) = Euchre();
        logic.GetAutoAdvanceDelay(state);       // settle whose bid it is

        var turned = state.Zones["kitty"].TopCard!;
        logic.Apply(state, new GameAction("bid_accept"));

        var dealerHand = state.Zones[$"hand:{state.DealerId}"];
        var taken = dealerHand.Cards.FirstOrDefault(c => c.Uid == turned.Uid);

        Assert.NotNull(taken);
        Assert.True(taken.IsFaceUp, "The card the dealer took up is face-down in their own hand.");
        Assert.All(dealerHand.Cards, c => Assert.True(c.IsFaceUp));
    }

    [Fact]
    public void A_meld_returned_to_hand_comes_back_face_up()
    {
        // Pinochle shows its melds for scoring and then picks them up again.
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("pinochle").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(2) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 4, []);

        for (int i = 0; i < 2000 && state.CurrentPhaseId != "play"; i++)
        {
            var cards = logic.GetSelectableCardIds(state);
            var actions = logic.GetValidActions(state);
            if (actions.FirstOrDefault(a => a.Type is "meld_done" or "continue") is { } done)
                logic.Apply(state, done);
            else if (actions.Count > 0) logic.Apply(state, actions[0]);
            else if (cards.Count > 0) logic.Apply(state, new GameAction("play_card", CardId: cards[0]));
            else break;
        }

        if (state.CurrentPhaseId != "play") return;   // never reached the pickup this deal

        foreach (var p in state.Players)
            Assert.All(state.Zones[$"hand:{p.Id}"].Cards,
                       c => Assert.True(c.IsFaceUp, $"{p.Id} holds {c.Id} face-down."));
    }
}

/// <summary>
/// What the game log records when cards move.
///
/// The log was a history of the status line, so it read as a list of whose turn it
/// was: "Player 3's bid", "Your turn | Trump: diamonds", and then "Dealer discarded"
/// with no word of the card the dealer had just been ordered up. A card is named where
/// the table could see it and not otherwise — naming a card off the deck would be the
/// log telling everyone something the game had not.
/// </summary>
public sealed class CardMovementLogTests
{
    private static (GameState State, IGameLogic Logic) Game(string id, int seats, ulong seed = 4)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(id).GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    [Fact]
    public void Taking_up_the_turned_card_says_which_card()
    {
        var (state, logic) = Game("euchre", 4);
        logic.GetAutoAdvanceDelay(state);

        var turned = state.Zones["kitty"].TopCard!;
        logic.Apply(state, new GameAction("bid_accept"));

        Assert.Contains(state.GameLog, line => line.Contains("took up") && line.Contains(GameText.CardName(turned)));
    }

    [Fact]
    public void The_dealers_discard_is_recorded_without_naming_it()
    {
        var (state, logic) = Game("euchre", 4);
        logic.GetAutoAdvanceDelay(state);

        var dealerHand = state.Zones[$"hand:{state.DealerId}"].Cards.ToList();
        logic.Apply(state, new GameAction("bid_accept"));
        while (state.CurrentPhaseId == "dealer_discard")
            logic.Apply(state, logic.GetAutoAction(state));

        Assert.Contains(state.GameLog, line => line.Contains("discarded a card"));

        // It went face-down onto the kitty; no line may name it.
        var buried = state.Zones["kitty"].TopCard!;
        Assert.DoesNotContain(state.GameLog, line => line.Contains(GameText.CardName(buried)));
    }

    [Fact]
    public void A_card_off_the_deck_is_never_named_and_one_off_the_discard_always_is()
    {
        var (state, logic) = Game("gin-rummy", 2, seed: 7);

        // Wind to a draw.
        for (int i = 0; i < 50 && !logic.GetValidActions(state).Any(a => a.Type == "draw_from_deck"); i++)
            logic.Apply(state, logic.GetAutoAction(state));

        var deckTop = state.Zones["deck"].TopCard!;
        logic.Apply(state, new GameAction("draw_from_deck"));

        Assert.Contains(state.GameLog, line => line.Contains("drew a card"));
        Assert.DoesNotContain(state.GameLog, line => line.Contains(GameText.CardName(deckTop)));
    }

    [Fact]
    public void A_card_taken_from_the_discard_is_named()
    {
        var (state, logic) = Game("gin-rummy", 2, seed: 7);

        for (int i = 0; i < 50 && !logic.GetValidActions(state).Any(a => a.Type == "draw_from_discard"); i++)
            logic.Apply(state, logic.GetAutoAction(state));
        if (!logic.GetValidActions(state).Any(a => a.Type == "draw_from_discard")) return;

        var top = state.Zones["discard"].TopCard!;
        logic.Apply(state, new GameAction("draw_from_discard"));

        Assert.Contains(state.GameLog, line => line.Contains("took the") && line.Contains(GameText.CardName(top)));
    }
}

/// <summary>
/// The dealer's own discard, and the mark that says who the dealer is.
/// </summary>
public sealed class DealerTests
{
    private static (GameState State, IGameLogic Logic) Euchre(ulong seed = 4)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("euchre").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 4, []);
        return (state, logic);
    }

    /// <summary>Winds to the dealer's discard with the dealer being the person at seat 0.</summary>
    private static (GameState State, IGameLogic Logic)? AtHumanDiscard()
    {
        for (ulong seed = 1; seed < 40; seed++)
        {
            var (state, logic) = Euchre(seed);
            logic.GetAutoAdvanceDelay(state);
            if (state.DealerId != state.Players[0].Id) continue;

            logic.Apply(state, new GameAction("bid_accept"));
            if (state.CurrentPhaseId == "dealer_discard") return (state, logic);
        }
        return null;
    }

    [Fact]
    public void A_tap_picks_the_card_and_a_button_throws_it()
    {
        // Reported: one click threw a card away for good, which is an easy thing to do
        // by accident in a hand you have just taken a card into.
        if (AtHumanDiscard() is not var (state, logic)) return;

        var hand = state.Zones[$"hand:{state.DealerId}"];
        var card = hand.Cards[2];
        int before = hand.Count;

        logic.Apply(state, new GameAction("select_card", CardId: card.Id, CardUid: card.Uid));

        Assert.Equal("dealer_discard", state.CurrentPhaseId);
        Assert.Equal(before, hand.Count);
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "discard" && a.Label is not null);

        logic.Apply(state, new GameAction("discard"));

        Assert.DoesNotContain(hand.Cards, c => c.Uid == card.Uid);
        Assert.Equal(before - 1, hand.Count);
        Assert.NotEqual("dealer_discard", state.CurrentPhaseId);
    }

    [Fact]
    public void A_second_tap_takes_the_pick_back()
    {
        if (AtHumanDiscard() is not var (state, logic)) return;

        var card = state.Zones[$"hand:{state.DealerId}"].Cards[1];
        logic.Apply(state, new GameAction("select_card", CardId: card.Id, CardUid: card.Uid));
        logic.Apply(state, new GameAction("select_card", CardId: card.Id, CardUid: card.Uid));

        Assert.False(state.Metadata.ContainsKey("selected_card"));
        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "discard");
    }

    [Fact]
    public void A_double_tap_throws_it_without_the_button()
    {
        if (AtHumanDiscard() is not var (state, logic)) return;

        var hand = state.Zones[$"hand:{state.DealerId}"];
        var card = hand.Cards[0];

        var shortcut = logic.GetDefaultCardAction(state, card.Id, card.Uid);
        Assert.NotNull(shortcut);

        logic.Apply(state, shortcut);
        Assert.DoesNotContain(hand.Cards, c => c.Uid == card.Uid);
    }

    [Fact]
    public void A_game_that_rotates_a_dealer_marks_one()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());

        // Unset means "when the game has a dealer at all", which is exactly the games
        // whose rounds rotate one.
        foreach (var id in new[] { "euchre", "pinochle", "hearts", "golf" })
        {
            var definition = loader.LoadAsync(id).GetAwaiter().GetResult()!;
            bool shows = definition.Ui?.ShowDealer
                      ?? (definition.Rounds?.Dealer is not null || definition.Rounds?.FirstDealer is not null);
            Assert.True(shows, $"{id} rotates a dealer but marks nobody.");
        }

        // War deals once and has no dealer to speak of.
        var war = loader.LoadAsync("war").GetAwaiter().GetResult()!;
        Assert.False(war.Ui?.ShowDealer
                  ?? (war.Rounds?.Dealer is not null || war.Rounds?.FirstDealer is not null));
    }
}

/// <summary>
/// What the log says about the two things a trick game does: naming trump, and playing
/// a card. Neither was in it — the log read as a list of whose turn it was, with the
/// trump suit repeated on every line and no word of what anybody played.
/// </summary>
public sealed class TrickLogTests
{
    private static (GameState State, IGameLogic Logic) Euchre(ulong seed = 4)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("euchre").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 4, []);
        return (state, logic);
    }

    [Fact]
    public void Naming_trump_gets_a_line_of_its_own_and_is_said_at_the_table()
    {
        var (state, logic) = Euchre();
        logic.GetAutoAdvanceDelay(state);

        string bidder = state.CurrentPlayer.Id;
        var turned = state.Zones["kitty"].TopCard!;
        logic.Apply(state, new GameAction("bid_accept"));

        string suit = turned.Suit.ToString();
        Assert.Contains(state.GameLog, l => l.Contains("ordered up") && l.Contains(suit));

        // And the player says it, beside their own seat, whoever is on turn by then.
        var said = Assert.Single(state.Announcements);
        Assert.Equal(bidder, said.PlayerId);
        Assert.Contains(suit, said.Text);
    }

    [Fact]
    public void Trump_called_in_the_second_round_is_announced_too()
    {
        var (state, logic) = Euchre(seed: 11);
        logic.GetAutoAdvanceDelay(state);

        // Everyone passes the turned card, then someone names a suit.
        for (int i = 0; i < 12 && state.CurrentPhaseId == "order_up"; i++)
            logic.Apply(state, new GameAction("bid_pass"));
        if (state.CurrentPhaseId != "call_trump") return;

        state.Announcements.Clear();
        string caller = state.CurrentPlayer.Id;
        var suit = logic.GetValidActions(state).First(a => a.Type.StartsWith("bid_")
                                                        && a.Type != "bid_pass"
                                                        && a.Type != "bid_alone");
        logic.Apply(state, suit);

        Assert.Contains(state.GameLog, l => l.Contains("named") && l.Contains("trump"));
        Assert.Contains(state.Announcements, a => a.PlayerId == caller);
    }

    [Fact]
    public void Every_card_played_face_up_is_named_in_the_log()
    {
        var (state, logic) = Euchre();
        logic.GetAutoAdvanceDelay(state);
        logic.Apply(state, new GameAction("bid_accept"));
        while (state.CurrentPhaseId == "dealer_discard")
            logic.Apply(state, logic.GetAutoAction(state));

        Assert.Equal("play", state.CurrentPhaseId);

        // Whoever leads may be a computer seat; take the first card the phase offers.
        var offered = logic.GetSelectableCardIds(state);
        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        var card = hand.Cards.FirstOrDefault(c => offered.Contains(c.Id));
        if (card is null) return;
        logic.Apply(state, new GameAction("play_card", CardId: card.Id, CardUid: card.Uid));

        Assert.Contains(state.GameLog, l => l.Contains("played") && l.Contains(GameText.CardName(card)));
    }
}
