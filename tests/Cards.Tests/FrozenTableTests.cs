using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The table must never stop.
///
/// Driving Hand and Foot to the end found a position with no legal move and an empty
/// action bar, held there for three thousand steps: a wild had claimed the discard
/// pile, the price of claiming is laying the card you claimed it for, and a wild is
/// never a meld by itself. Every route out of the turn was barred behind a debt that
/// could not be paid. A person meets the same wall — the symptom is "it is my turn and
/// I have no options", which is how it was reported in another game.
/// </summary>
public sealed class FrozenTableTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot(int seats = 2)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(11) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    /// <summary>A hand and a pile staged so the top card is claimable on its own terms.</summary>
    private static void StagePile(GameState state, Card top, params Card[] hand)
    {
        var mine = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        mine.Clear();
        foreach (var card in hand) mine.Add(card);

        var discard = state.Zones["discard"];
        discard.Clear();
        discard.Add(new Card(Suit.Diamonds, Rank.Nine, isFaceUp: true) { Uid = 9001 });
        top.IsFaceUp = true;
        discard.Add(top);
    }

    [Fact]
    public void A_pile_topped_by_a_wild_cannot_be_claimed()
    {
        // Two is wild here. Holding two more of them satisfies the letter of the pickup
        // condition — "two cards matching the top" — and the pile still may not be
        // taken, because the card taken must be laid and a wild cannot be laid alone.
        var (state, logic) = HandAndFoot();
        StagePile(state,
            new Card(Suit.Spades, Rank.Two) { Uid = 9002 },
            new Card(Suit.Clubs,  Rank.Two) { Uid = 9003 },
            new Card(Suit.Hearts, Rank.Two) { Uid = 9004 },
            new Card(Suit.Clubs,  Rank.King) { Uid = 9005 },
            new Card(Suit.Spades, Rank.King) { Uid = 9006 });

        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "draw_from_discard");
    }

    [Fact]
    public void A_pile_topped_by_a_joker_cannot_be_claimed_either()
    {
        var (state, logic) = HandAndFoot();
        StagePile(state,
            new Card(Suit.Spades, Rank.Joker) { Uid = 9007 },
            new Card(Suit.Clubs,  Rank.Joker) { Uid = 9008 },
            new Card(Suit.Hearts, Rank.Joker) { Uid = 9009 });

        Assert.DoesNotContain(logic.GetValidActions(state), a => a.Type == "draw_from_discard");
    }

    [Fact]
    public void A_debt_that_cannot_be_paid_is_forgiven_rather_than_freezing_the_turn()
    {
        // Straight to the position that froze: a debt owed for a card the hand cannot
        // possibly lay. However it was reached — an older save holds one — the turn has
        // to remain playable.
        var (state, logic) = HandAndFoot();
        var mine = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        mine.Clear();
        mine.Add(new Card(Suit.Spades, Rank.Two)  { Uid = 9101 });   // wild: unmeldable alone
        mine.Add(new Card(Suit.Clubs,  Rank.Two)  { Uid = 9102 });
        mine.Add(new Card(Suit.Hearts, Rank.Five) { Uid = 9103 });

        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["dd_must_meld"]  = "2s";

        logic.GetValidActions(state);

        Assert.False(state.Metadata.ContainsKey("dd_must_meld"), "The debt froze the turn.");
        Assert.Contains(state.GameLog, l => l.Contains("could not meld"));

        // And the turn can be ended: pick a card, and discarding is offered again.
        var five = mine.Cards.Single(c => c.Rank == Rank.Five);
        logic.Apply(state, new GameAction("select_card", CardId: five.Id, CardUid: five.Uid));
        Assert.Contains(logic.GetValidActions(state), a => a.Type == "discard");
    }

    [Fact]
    public void A_debt_that_can_be_paid_is_still_exacted()
    {
        // The rule comes first. Three aces are worth sixty, which opens this round, so
        // the ace owed can be laid — the obligation stands and the turn cannot be ended
        // around it. (Three kings would be thirty, below the fifty this round asks, and
        // that debt really is unpayable.)
        var (state, logic) = HandAndFoot();
        var mine = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        mine.Clear();
        mine.Add(new Card(Suit.Spades,   Rank.Ace)  { Uid = 9201 });
        mine.Add(new Card(Suit.Clubs,    Rank.Ace)  { Uid = 9202 });
        mine.Add(new Card(Suit.Hearts,   Rank.Ace)  { Uid = 9203 });
        mine.Add(new Card(Suit.Diamonds, Rank.Five) { Uid = 9204 });

        state.Metadata["dd_turn_state"] = "discard";
        state.Metadata["dd_must_meld"]  = "As";

        var actions = logic.GetValidActions(state);

        Assert.True(state.Metadata.ContainsKey("dd_must_meld"), "A payable debt was forgiven.");
        Assert.Contains(actions, a => a.Type == "meld");
        Assert.DoesNotContain(actions, a => a.Type == "discard");
    }

    [Fact]
    public void Hand_and_foot_plays_on_rather_than_stopping()
    {
        // The whole game, driven: it used to reach a position it never left. It need not
        // finish inside the budget — the computer players cannot meld well enough for
        // that — but it must keep producing new positions rather than one forever.
        var (state, logic) = HandAndFoot();

        int tap = 0;
        var recent = new Queue<string>();

        for (int i = 0; i < 2500 && !logic.IsGameOver(state); i++)
        {
            if (TableDriver.Step(state, logic, ref tap) != TableDriver.StepResult.Moved) break;

            recent.Enqueue($"{state.CurrentPhaseId}|{state.CurrentPlayerIndex}|{state.RoundNumber}|"
                         + string.Join(",", state.Zones.OrderBy(z => z.Key, StringComparer.Ordinal)
                                                       .Select(z => z.Value.Count)));
            if (recent.Count > 400) recent.Dequeue();
        }

        Assert.True(recent.Distinct().Count() > 20,
            $"The table stopped: {recent.Distinct().Count()} distinct positions in the last {recent.Count} steps.");
    }
}
