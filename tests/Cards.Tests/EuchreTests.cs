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
    private static (GameState State, IGameLogic Logic) Table(string id = "euchre-4p", int seats = 4)
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
        var vm = await Playing("euchre-4p", 4);

        Assert.Contains(vm.Logic!.GetValidActions(vm.State!), a => a.Type == "tap");
        Assert.DoesNotContain(vm.Actions, a => a.Type == "tap");
        Assert.All(vm.Actions, a => Assert.NotNull(a.Label));
    }

    [Fact]
    public async Task A_double_tap_plays_the_card()
    {
        var vm = await Playing("euchre-4p", 4);
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
        var vm = await Playing("euchre-4p", 4);
        var state = vm.State!;
        string me = state.CurrentPlayer.Id;

        var offered = vm.SelectableCardIds.ToHashSet();
        var refused = state.Zones[$"hand:{me}"].Cards.FirstOrDefault(c => !offered.Contains(c.Id));
        if (refused is null) return;   // every card was legal this deal; nothing to prove

        await vm.ActivateCard(refused.Id, refused.Uid);
        Assert.Contains(state.Zones[$"hand:{me}"].Cards, c => c.Uid == refused.Uid);
    }
}
