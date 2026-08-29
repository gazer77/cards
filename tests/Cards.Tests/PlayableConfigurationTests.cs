using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Every game, at every player count it advertises support for, must be able to make
/// at least one move once initialized. A game that offers a seat count it cannot
/// actually play is dead on arrival for the player who picks it.
/// </summary>
public sealed class PlayableConfigurationTests
{
    private static readonly string RepoRoot = FileSystemGameAssetSource.FindRepoRoot();

    private static GameLoader NewLoader() => new(new FileSystemGameAssetSource(RepoRoot));

    public static TheoryData<string, int> EverySupportedSeatCount
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var def in NewLoader().LoadAllAsync().GetAwaiter().GetResult())
                for (int n = def.MinPlayers; n <= def.MaxPlayers; n++)
                    data.Add(def.Id, n);
            return data;
        }
    }

    /// <summary>
    /// Configurations known to be broken today, kept green so the suite stays meaningful.
    ///
    /// hand-and-foot 4p/5p/6p: the definition uses "standard-104" (104 cards) and deals
    /// stacks_per_player 2 × cards_per_stack 13 = 26 cards each. Four players consume the
    /// entire deck, leaving nothing for "then_flip_top_to": "discard"; five and six
    /// exhaust it mid-deal. The game stalls in phase "play" with no legal move.
    /// Real Hand and Foot uses five or six decks — fixing this needs a larger deck type
    /// in DeckBuilder, which today tops out at standard-104.
    /// </summary>
    /// <summary>
    /// Configurations known not to work, asserted to be still broken so that whoever
    /// fixes one is told to remove the exemption.
    ///
    /// Empty: Hand and Foot at 4-6 players was the only entry, and it now deals from a
    /// deck that scales with the table rather than a fixed 104 cards.
    /// </summary>
    private static readonly HashSet<(string Game, int Players)> KnownBroken = [];

    [Theory]
    [MemberData(nameof(EverySupportedSeatCount))]
    public async Task Advertised_player_count_can_make_a_move(string gameId, int playerCount)
    {
        // free-play is a rules-free sandbox: it legitimately has nothing to auto-advance.
        if (gameId == "free-play") return;

        var result = await EngineRunner.RunAsync(NewLoader(), gameId, playerCount, 1UL);
        bool moved = result.Steps > 0;

        if (KnownBroken.Contains((gameId, playerCount)))
        {
            Assert.False(moved,
                $"'{gameId}' at {playerCount} players now works. Remove it from " +
                $"{nameof(KnownBroken)} so the fix stays locked in.");
            return;
        }

        Assert.True(moved,
            $"'{gameId}' advertises support for {playerCount} players but made 0 moves — " +
            $"it stalls immediately in phase '{result.FinalPhase}'.");
    }
}
