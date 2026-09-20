using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// A grouped zone's slots are the definition's: which cards each holds, in what order.
/// A group goes by its defining natural rank; one with no natural card goes by wild —
/// which is how "4s and their wilds" and "wilds only" are both expressible.
/// </summary>
public sealed class ZoneSlotTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(9),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);
        return (state, logic);
    }

    private static Zone Melds(GameState state) => ZoneIntake.SideZone(state, "meld", state.CurrentPlayer.Id)!;

    [Fact]
    public void Hand_and_foot_declares_its_strip_in_full()
    {
        var (state, _) = HandAndFoot();
        var slots = ZoneSlots.For(Melds(state), state);

        Assert.Equal(13, slots.Count);
        Assert.Equal("3", slots[0].Label);
        Assert.Equal("W", slots[^1].Label);
        Assert.True(slots[^1].Match.Wild);
    }

    [Fact]
    public void A_meld_goes_by_its_natural_rank_wilds_and_all()
    {
        var (state, _) = HandAndFoot();
        var slots = ZoneSlots.For(Melds(state), state);
        var wilds = MeldRules.WildRanks(state.Definition);

        var fours = new List<Card>
        {
            new(Suit.Clubs,  Rank.Four) { Uid = 1 },
            new(Suit.Hearts, Rank.Four) { Uid = 2 },
            new(Suit.Spades, Rank.Two)  { Uid = 3 },   // a wild, along for the ride
        };
        Assert.Equal("4", slots[ZoneSlots.SlotOf(slots, fours, wilds)].Label);

        var onlyWilds = new List<Card>
        {
            new(Suit.Clubs, Rank.Two)   { Uid = 4 },
            new(Suit.Clubs, Rank.Joker) { Uid = 5, IsWild = true },
        };
        Assert.Equal("W", slots[ZoneSlots.SlotOf(slots, onlyWilds, wilds)].Label);

        var redThree = new List<Card> { new(Suit.Hearts, Rank.Three) { Uid = 6 } };
        Assert.Equal("3", slots[ZoneSlots.SlotOf(slots, redThree, wilds)].Label);
    }

    /// <summary>A black three has no slot — it is a discard, never filed and never melded.</summary>
    [Fact]
    public void A_card_no_slot_claims_has_no_slot()
    {
        var (state, _) = HandAndFoot();
        var slots = ZoneSlots.For(Melds(state), state);
        var wilds = MeldRules.WildRanks(state.Definition);

        Assert.Equal(-1, ZoneSlots.SlotOf(slots, new Card(Suit.Spades, Rank.Three) { Uid = 7 }, wilds));
    }

    /// <summary>
    /// Seven red threes in their slot are not a book and are not meld value; they are
    /// worth exactly the bonus the definition names, once.
    /// </summary>
    [Fact]
    public void Filed_threes_are_a_bonus_not_a_meld()
    {
        var (state, logic) = HandAndFoot();
        var me    = state.CurrentPlayer.Id;
        var melds = Melds(state);
        melds.Clear();

        var suits = new[] { Suit.Hearts, Suit.Diamonds };
        melds.AddGroup(Enumerable.Range(0, 7).Select(i => new Card(suits[i % 2], Rank.Three) { Uid = 100 + i }));

        Assert.Equal(0, RuleCondition.CountBooks(melds, state));
        Assert.DoesNotContain("go_out", logic.GetValidActions(state).Select(a => a.Type));

        // Score the round: 7 × 100, and nothing else for those cards.
        state.Zones[$"hand:{me}"].Clear();
        state.Zones[$"foot:{me}"].Clear();
        ScoringEngine.Apply(state);
        int mine = state.Scores.GetValueOrDefault(me);
        Assert.Equal(700, mine);
    }
}
