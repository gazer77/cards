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
