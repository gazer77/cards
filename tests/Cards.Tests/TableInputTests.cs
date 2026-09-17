using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The ways a player can say what they want, and whether the table offers them.
///
/// A table once announced "Your turn — Draw a card" while offering no button and no
/// working tap target: a lone action deliberately had no button, and the deck's own top
/// card swallowed taps meant for the deck. The only way through was tapping bare felt,
/// which nothing on screen mentioned.
/// </summary>
public sealed class TableInputTests
{
    private static (GameState State, IGameLogic Logic) HandAndFoot(int seats = 2)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(11),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static List<string> ActionTypes(GameState state, IGameLogic logic)
        => logic.GetValidActions(state).Select(a => a.Type).ToList();

    /// <summary>
    /// The bug as reported: it is the player's turn to draw and the game must offer a
    /// way to do it. Naming the action after its zone is what lets a double-tap on the
    /// deck find it without the client hardcoding "deck".
    /// </summary>
    [Fact]
    public void Drawing_is_offered_by_a_zone_named_action()
    {
        var (state, logic) = HandAndFoot();

        Assert.Contains("draw_from_deck", ActionTypes(state, logic));
    }

    /// <summary>
    /// Claiming the discard pile is gated: this side has not melded, so it must not be
    /// offered — the table should never show an action it will refuse.
    /// </summary>
    [Fact]
    public void A_gated_draw_is_not_offered_before_its_condition_holds()
    {
        var (state, logic) = HandAndFoot();

        Assert.DoesNotContain("draw_from_discard", ActionTypes(state, logic));
    }

    [Fact]
    public void Discarding_is_offered_once_the_selection_is_the_right_size()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        // Nothing selected: the definition asks for one card, and none is picked.
        Assert.DoesNotContain("discard", ActionTypes(state, logic));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        state.Metadata["selected_card"] = hand.Cards[0].Id;
        Assert.Contains("discard", ActionTypes(state, logic));

        // Two cards is a meld in progress, not a discard.
        state.Metadata["selected_card"] = $"{hand.Cards[0].Id},{hand.Cards[1].Id}";
        Assert.DoesNotContain("discard", ActionTypes(state, logic));
    }

    /// <summary>
    /// The Discard button carries no card — the selection is the card. This fell
    /// through to a guard that skips the discard fallback in melding games, so the
    /// button did nothing at all.
    /// </summary>
    [Fact]
    public void The_discard_button_discards_the_selected_card()
    {
        var (state, logic) = HandAndFoot();
        logic.Apply(state, new GameAction("draw_from_deck"));

        var hand = state.Zones[$"hand:{state.CurrentPlayer.Id}"];
        var card = hand.Cards[0];
        int before = hand.Count;

        state.Metadata["selected_card"] = card.Id;
        logic.Apply(state, new GameAction("discard"));

        Assert.Equal(before - 1, hand.Count);
        Assert.Equal(card.Id, state.Zones["discard"].TopCard!.Id);
    }
}
