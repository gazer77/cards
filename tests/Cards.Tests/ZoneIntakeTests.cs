using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Rules that fire as cards arrive in a zone — Hand and Foot's red threes, which go to
/// the threes pile the moment they reach a hand, by any route, and are replaced.
/// </summary>
public sealed class ZoneIntakeTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot(ulong seed = 5)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(seed),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);
        return (state, logic);
    }

    private static bool IsRedThree(Card c) => c.Rank == Rank.Three && c.IsRed;

    /// <summary>The rule is "when it reaches your hand", so a fresh deal has none in any hand.</summary>
    [Theory]
    [InlineData(1UL)] [InlineData(7UL)] [InlineData(42UL)] [InlineData(20260919UL)]
    public void No_hand_holds_a_red_three_after_the_deal(ulong seed)
    {
        var (state, _) = HandAndFoot(seed);

        foreach (var p in state.Players)
        {
            var hand = state.Zones[$"hand:{p.Id}"];
            Assert.DoesNotContain(hand.Cards, IsRedThree);
            Assert.Equal(13, hand.Count);   // replaced, not merely removed
        }
    }

    [Fact]
    public void A_drawn_red_three_goes_to_the_pile_and_is_replaced()
    {
        var (state, logic) = HandAndFoot();
        var me     = state.CurrentPlayer.Id;
        var hand   = state.Zones[$"hand:{me}"];
        var deck   = state.Zones["deck"];
        var threes = ZoneIntake.SideZone(state, "threes", me)!;

        int threesBefore = threes.Count;
        int handBefore   = hand.Count;

        // Stack the deck: the next two draws are a red three and a plain card.
        deck.Add(new Card(Suit.Clubs,  Rank.Nine)  { Uid = 7001 });   // replacement (drawn last)
        deck.Add(new Card(Suit.Hearts, Rank.Three) { Uid = 7002 });   // drawn first... 
        // draw_count from deck is 2, so both come; the three leaves and one more is drawn.
        deck.Add(new Card(Suit.Spades, Rank.Eight) { Uid = 7003 });

        logic.Apply(state, new GameAction("draw_from_deck"));

        Assert.Equal(threesBefore + 1, threes.Count);
        Assert.Contains(threes.Cards, c => c.Uid == 7002);
        Assert.DoesNotContain(hand.Cards, IsRedThree);
        Assert.Equal(handBefore + 2, hand.Count);   // two drawn, one replaced: still +2 in hand
    }

    [Fact]
    public void A_red_three_revealed_from_the_foot_goes_to_the_pile()
    {
        var (state, logic) = HandAndFoot();
        state.RoundNumber = 1;
        logic.Apply(state, new GameAction("draw_from_deck"));

        var me     = state.CurrentPlayer.Id;
        var hand   = state.Zones[$"hand:{me}"];
        var foot   = state.Zones[$"foot:{me}"];
        var threes = ZoneIntake.SideZone(state, "threes", me)!;

        foot.Clear();
        foot.Add(new Card(Suit.Diamonds, Rank.Three) { Uid = 7101 });
        foot.Add(new Card(Suit.Clubs,    Rank.Nine)  { Uid = 7102 });

        // Meld away the whole hand so the foot is picked up.
        hand.Clear();
        var aces = new[]
        {
            new Card(Suit.Clubs,  Rank.Ace) { Uid = 7111 },
            new Card(Suit.Hearts, Rank.Ace) { Uid = 7112 },
            new Card(Suit.Spades, Rank.Ace) { Uid = 7113 },
        };
        foreach (var c in aces) hand.Add(c);
        state.Metadata["selected_card"] = string.Join(",", aces.Select(c => c.Uid));
        int threesBefore = threes.Count;

        logic.Apply(state, new GameAction("meld"));

        Assert.Equal(threesBefore + 1, threes.Count);
        Assert.DoesNotContain(hand.Cards, IsRedThree);
        Assert.Contains(hand.Cards, c => c.Uid == 7102);   // the rest of the foot arrived
    }

    /// <summary>A black three is an ordinary (unmeldable) card and stays put.</summary>
    [Fact]
    public void A_black_three_stays_in_hand()
    {
        var (state, logic) = HandAndFoot();
        var me   = state.CurrentPlayer.Id;
        var hand = state.Zones[$"hand:{me}"];
        var deck = state.Zones["deck"];

        deck.Add(new Card(Suit.Clubs,  Rank.Nine)  { Uid = 7201 });
        deck.Add(new Card(Suit.Spades, Rank.Three) { Uid = 7202 });

        logic.Apply(state, new GameAction("draw_from_deck"));

        Assert.Contains(hand.Cards, c => c.Uid == 7202);
    }
}
