using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Go Fish invariants, driven by playing real games.
///
/// Go Fish is the one game where cards move between hands rather than to a shared
/// pile, so a mistake shows up as cards quietly leaving the game rather than as a
/// crash — the table still looks plausible while nobody holds what they should.
/// </summary>
public sealed class GoFishTests
{
    private const int Deck = 52;

    private static int TotalCards(GameState s) => s.Zones.Values.Sum(z => z.Count);

    private static (GameState State, IGameLogic Logic) Start(ulong seed, int players = 2)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId     = definition.Id,
            Definition = definition,
            Rng        = new SeededRandomSource(seed),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, []);
        return (state, logic);
    }

    /// <summary>
    /// Advances one step the way a player would, returning false when the game offers
    /// nothing to do.
    ///
    /// Go Fish cannot be driven by GetAutoAction alone: on the player's turn it offers
    /// no actions until a card has been tapped, because tapping is how you choose the
    /// rank to ask for. A harness that only applies valid actions therefore sits on the
    /// opening deal forever — which is why Go Fish is one of the games that ran to the
    /// step cap when the golden masters were recorded.
    /// </summary>
    private static bool Step(GameState state, IGameLogic logic)
    {
        var actions = logic.GetValidActions(state);
        if (actions.Count > 0)
        {
            logic.Apply(state, logic.GetAutoAction(state));
            return true;
        }

        var selectable = logic.GetSelectableCardIds(state);
        if (selectable.Count > 0)
        {
            logic.Apply(state, new GameAction("select_card", CardId: selectable[0]));
            return true;
        }

        return false;
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(99UL)]
    public void No_card_ever_leaves_the_game(ulong seed)
    {
        var (state, logic) = Start(seed);

        Assert.Equal(Deck, TotalCards(state));

        for (int step = 0; step < 3000 && !logic.IsGameOver(state); step++)
        {
            if (!Step(state, logic)) break;

            Assert.True(TotalCards(state) == Deck,
                $"Cards left the game at step {step}: {TotalCards(state)} of {Deck}.");
        }
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(99UL)]
    public void A_game_reaches_an_end(ulong seed)
    {
        var (state, logic) = Start(seed);

        int step = 0;
        for (; step < 3000 && !logic.IsGameOver(state); step++)
        {
            if (!Step(state, logic)) break;
        }

        Assert.True(logic.IsGameOver(state),
            $"Go Fish did not finish in {step} steps. Deck {state.Zones["deck"].Count}, " +
            string.Join(", ", state.Zones
                .Where(z => z.Key.StartsWith("hand:") || z.Key.StartsWith("books:"))
                .Select(z => $"{z.Key}={z.Value.Count}")));
    }

    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void Books_are_four_of_a_kind(ulong seed)
    {
        var (state, logic) = Start(seed);

        for (int step = 0; step < 3000 && !logic.IsGameOver(state); step++)
        {
            if (!Step(state, logic)) break;

            foreach (var (id, zone) in state.Zones.Where(z => z.Key.StartsWith("books:")))
            {
                Assert.True(zone.Count % 4 == 0,
                    $"{id} holds {zone.Count} cards, which is not whole books.");

                foreach (var group in zone.Cards.GroupBy(c => c.Rank))
                    Assert.True(group.Count() == 4,
                        $"{id} holds {group.Count()} {group.Key}s — a book must be all four.");
            }
        }
    }

    /// <summary>
    /// The AI used to ask for the same rank indefinitely. It remembered ranks it had
    /// seen the player hold, but not ranks the player had refused, and its fallback
    /// pick — "the rank I hold most of" — is deterministic. Every failed ask draws a
    /// card, so it emptied the deck repeating one question that could not succeed.
    /// </summary>
    [Theory]
    [InlineData(7UL)]
    [InlineData(21UL)]
    public void The_ai_does_not_repeat_a_refused_rank(ulong seed)
    {
        var (state, logic) = Start(seed);

        var asks = new List<string>();
        string last = "";

        for (int step = 0; step < 3000 && !logic.IsGameOver(state); step++)
        {
            if (!Step(state, logic)) break;

            string status = state.Metadata.GetValueOrDefault("status", "");
            if (status == last) continue;
            last = status;

            const string prefix = "AI asked for ";
            if (status.StartsWith(prefix))
                asks.Add(status[prefix.Length..].Split(' ')[0]);
        }

        Assert.True(asks.Count > 5, $"Only {asks.Count} AI asks seen; too few to judge.");

        // Some repetition is correct — a successful ask goes again, and a rank becomes
        // worth retrying once the player has drawn. A long run of one rank is the bug.
        int longest = 1, run = 1;
        for (int i = 1; i < asks.Count; i++)
        {
            run = asks[i] == asks[i - 1] ? run + 1 : 1;
            longest = Math.Max(longest, run);
        }

        Assert.True(longest <= 3,
            $"AI asked for the same rank {longest} times running: {string.Join(", ", asks.Take(20))}");
    }

    /// <summary>
    /// Refusals must not be remembered forever either: the player draws unseen cards,
    /// so a rank ruled out early has to become askable again or the AI goes blind to
    /// most of the deck as the game goes on.
    /// </summary>
    [Fact]
    public void Refusals_expire_as_the_player_draws()
    {
        var (state, logic) = Start(7UL);

        int maxDenied = 0;
        for (int step = 0; step < 3000 && !logic.IsGameOver(state); step++)
        {
            if (!Step(state, logic)) break;

            var denied = state.Metadata.GetValueOrDefault("gf_denied_p0", "");
            int count = denied.Length == 0 ? 0 : denied.Split(',').Length;
            maxDenied = Math.Max(maxDenied, count);
        }

        // Thirteen ranks; if the list ever reached all of them the AI would have
        // nothing left it considers worth asking for.
        Assert.True(maxDenied < 13,
            $"The AI ruled out {maxDenied} of 13 ranks — refusals are never expiring.");
    }


    /// <summary>
    /// Go Fish is capped at two players because <c>GoFishHandler</c> is written for two
    /// seats: it addresses <c>Players[0]</c> and <c>Players[1]</c> directly, so at three
    /// or more, every other seat is dealt cards and then never asked, never given a
    /// turn, and never able to give a card back. Their cards leave circulation, the deck
    /// drains, and the game cannot finish — measured at 3, 4 and 6 players before the
    /// cap went in.
    ///
    /// Delete this and raise the cap together with a handler that rotates turns over all
    /// seats, lets the asker choose a target, and keeps its memory per opponent.
    /// </summary>
    [Fact]
    public void Is_capped_at_two_players_until_the_handler_supports_more()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;

        Assert.Equal(2, definition.MaxPlayers);
    }
}
