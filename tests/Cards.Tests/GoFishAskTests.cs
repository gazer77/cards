using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Asking for a rank the opponent holds must hand the cards over.
///
/// Built as an exact position rather than played into, because the reported failure —
/// three aces held, the fourth provably in the opponent's hand, and "Go Fish" — is a
/// late-game state that random play rarely reaches.
/// </summary>
public sealed class GoFishAskTests
{
    private static (GameState State, IGameLogic Logic) Position()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(1),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);

        // Clear the dealt position and build the reported one.
        foreach (var z in state.Zones.Values) z.Clear();

        var you = state.Zones[$"hand:{state.Players[0].Id}"];
        var ai  = state.Zones[$"hand:{state.Players[1].Id}"];

        foreach (var suit in new[] { Suit.Clubs, Suit.Diamonds, Suit.Spades })
            you.Add(new Card(suit, Rank.Ace, isFaceUp: true));

        // The fourth ace, and some filler so the AI is not empty.
        ai.Add(new Card(Suit.Hearts, Rank.Ace));
        ai.Add(new Card(Suit.Clubs, Rank.Five));
        ai.Add(new Card(Suit.Spades, Rank.Seven));

        state.Metadata["gf_state"] = "player_turn";
        return (state, logic);
    }

    [Fact]
    public void Asking_for_a_rank_the_opponent_holds_hands_it_over()
    {
        var (state, logic) = Position();

        var you = state.Zones[$"hand:{state.Players[0].Id}"];
        var ai  = state.Zones[$"hand:{state.Players[1].Id}"];

        // Tap an ace, then ask — exactly what the table does.
        logic.Apply(state, new GameAction("select_card", CardId: you.Cards[0].Id));

        var ask = logic.GetValidActions(state).FirstOrDefault(a => a.Type == "ask");
        Assert.NotNull(ask);

        logic.Apply(state, ask!);

        Assert.False(ai.Cards.Any(c => c.Rank == Rank.Ace),
            "The AI still holds an ace after being asked for aces.");

        // Four aces makes a book, so they leave the hand for the books pile.
        var books = state.Zones[$"books:{state.Players[0].Id}"];
        Assert.Equal(4, books.Cards.Count(c => c.Rank == Rank.Ace));
    }

    /// <summary>
    /// The deck being empty must not change the answer. A player with three of a rank
    /// and the fourth in the opponent's hand should always be able to win it.
    /// </summary>
    [Fact]
    public void An_empty_deck_does_not_stop_a_successful_ask()
    {
        var (state, logic) = Position();

        state.Zones["deck"].Clear();

        var you = state.Zones[$"hand:{state.Players[0].Id}"];
        logic.Apply(state, new GameAction("select_card", CardId: you.Cards[0].Id));
        logic.Apply(state, logic.GetValidActions(state).First(a => a.Type == "ask"));

        string status = state.Metadata.GetValueOrDefault("status", "");
        Assert.DoesNotContain("Go Fish", status);
    }
}
