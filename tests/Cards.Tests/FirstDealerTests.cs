using System.Text.Json;
using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// Who deals first, the way a table settles it: dealing face up round the table until
/// the card the game names turns up — the first jack in Euchre, the first ace in Poker —
/// or high or low card. Every card dealt is in the log, so the table can see how it went.
/// </summary>
public sealed class FirstDealerTests
{
    private static GameState Start(string gameId, int seats, ulong seed, Action<GameDefinition>? change = null)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), gameId)!;
        change?.Invoke(definition);
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        LogicRegistry.Create(definition).Initialize(state, seats, []);
        return state;
    }

    /// <summary>The cards dealt for the deal, as the log tells them: who, and what.</summary>
    private static List<(string Name, string Card)> Dealt(GameState state)
        => state.GameLog
            .Select(l => GameText.Render(state, l, null))
            .Where(l => l.Contains(" drew the ") && !l.Contains("deals first"))
            .Select(l => (l[..l.IndexOf(" drew the ")], l[(l.IndexOf(" drew the ") + 10)..]))
            .ToList();

    [Theory]
    [InlineData(1UL)] [InlineData(7UL)] [InlineData(42UL)]
    public void Euchre_deals_to_the_first_jack(ulong seed)
    {
        var state = Start("euchre", 4, seed);
        var dealt = Dealt(state);

        // Round the table from the first seat, one at a time, and no jack before the last.
        var names = state.Players.Select(p => p.Name).ToList();
        for (int i = 0; i < dealt.Count; i++)
            Assert.Equal(names[i % names.Count], dealt[i].Name);
        Assert.All(dealt.SkipLast(1), d => Assert.False(d.Card.StartsWith("Jack"), $"{d.Name} drew the {d.Card} before the jack."));
        Assert.StartsWith("Jack", dealt[^1].Card);

        // Whoever got it deals, and says so beside their seat.
        var dealer = state.Players.Single(p => p.Id == state.DealerId);
        Assert.Equal(dealer.Name, dealt[^1].Name);
        Assert.Contains(state.Announcements, a => a.PlayerId == dealer.Id && a.Text.Contains("deal"));
    }

    [Fact]
    public void A_rule_can_name_the_first_black_ace()
    {
        var state = Start("hearts", 4, 3UL, d => d.Rounds!.FirstDealer = new FirstDealerDefinition
        {
            Mode = "deal_until", DealUntil = new CardMatch { Rank = "A", Color = "black" },
        });

        var last = Dealt(state)[^1];
        Assert.True(last.Card is "Ace of Spades" or "Ace of Clubs", $"Dealt to {last.Card}.");
        Assert.All(Dealt(state).SkipLast(1), d => Assert.False(d.Card is "Ace of Spades" or "Ace of Clubs"));
        Assert.Equal(last.Name, state.Players.Single(p => p.Id == state.DealerId).Name);
    }

    [Theory]
    [InlineData("high_card")] [InlineData("low_card")]
    public void High_or_low_card_deals_everyone_one_and_the_tied_draw_again(string mode)
    {
        for (ulong seed = 1; seed <= 20; seed++)
        {
            var state = Start("hearts", 4, seed, d => d.Rounds!.FirstDealer = new FirstDealerDefinition { Mode = mode });
            var dealt = Dealt(state);
            Assert.True(dealt.Count >= 4);

            // Replay the log: everyone draws; the best card deals, or the tied draw again.
            int Value(string card) => card.Split(' ')[0] switch
            {
                "Jack" => 11, "Queen" => 12, "King" => 13, "Ace" => mode == "high_card" ? 14 : 1,
                var n  => int.Parse(n),
            };

            var drawing = state.Players.Select(p => p.Name).ToList();
            int at = 0;
            string? winner = null;
            while (winner is null)
            {
                var draw = dealt.Skip(at).Take(drawing.Count).ToList();
                Assert.Equal(drawing, draw.Select(d => d.Name).ToList());
                at += draw.Count;

                int best = mode == "high_card" ? draw.Max(d => Value(d.Card)) : draw.Min(d => Value(d.Card));
                var tied = draw.Where(d => Value(d.Card) == best).Select(d => d.Name).ToList();
                if (tied.Count == 1) winner = tied[0];
                else drawing = tied;
            }

            Assert.Equal(dealt.Count, at);   // nothing dealt after it was settled
            Assert.Equal(winner, state.Players.Single(p => p.Id == state.DealerId).Name);
        }
    }

    [Fact]
    public void A_game_that_says_nothing_still_picks_at_random()
    {
        var state = Start("hearts", 4, 5UL);
        Assert.NotNull(state.DealerId);
        Assert.Empty(Dealt(state));
        Assert.Null(state.FirstDealerDraw);   // nothing for the table to show
    }

    /// <summary>
    /// The cards dealt for the deal are kept, in order, for the table to deal out on screen
    /// before the real shuffle: the same cards the log names, ending with the one that
    /// decided it, in the hand of the player who now deals.
    /// </summary>
    [Fact]
    public void The_draw_is_kept_for_the_table_to_show()
    {
        var state = Start("euchre", 4, 7UL);
        var draw  = state.FirstDealerDraw!;

        Assert.Equal(Dealt(state).Count, draw.Count);
        Assert.Equal(Dealt(state).Select(d => d.Card), draw.Select(d => GameText.CardName(d.Card)));
        Assert.Equal(Rank.Jack, draw[^1].Card.Rank);
        Assert.Equal(state.DealerId, draw[^1].PlayerId);

        // Round the table from the first seat, one card each.
        for (int i = 0; i < draw.Count; i++)
            Assert.Equal(state.Players[i % state.Players.Count].Id, draw[i].PlayerId);
    }

    [Theory]
    [InlineData("\"high_card\"", "high_card")]
    [InlineData("{ \"deal_until\": { \"rank\": \"J\" } }", "deal_until")]
    public void It_reads_as_a_word_or_a_rule_and_writes_back_the_same(string json, string mode)
    {
        var read = JsonSerializer.Deserialize<FirstDealerDefinition>(json)!;
        Assert.Equal(mode, read.Mode);

        var again = JsonSerializer.Deserialize<FirstDealerDefinition>(JsonSerializer.Serialize(read))!;
        Assert.Equal(mode, again.Mode);
        Assert.Equal(read.DealUntil?.Rank, again.DealUntil?.Rank);
    }

    [Fact]
    public void A_first_dealer_the_engine_cannot_keep_fails_the_definition()
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hearts")!;

        definition.Rounds!.FirstDealer = new FirstDealerDefinition { Mode = "first_joker" };
        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("first_dealer"));

        definition.Rounds.FirstDealer = new FirstDealerDefinition { Mode = "deal_until", DealUntil = new CardMatch() };
        Assert.Contains(DefinitionValidator.Validate(definition), p => p.Contains("deal_until names no card"));
    }
}
