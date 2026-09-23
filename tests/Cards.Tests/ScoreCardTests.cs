using Cards.Engine;
using Cards.Rendering;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// The score card a definition places on the table, and the per-round history behind it.
///
/// Scores lived only in a total and an end-of-game list, so a game of nine holes could
/// not show the holes and a game mid-round showed nothing at all.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class ScoreCardTests
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    private static (GameState State, IGameLogic Logic) Game(string id, int seats)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync(id).GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(7) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    [Fact]
    public void Golf_declares_a_score_card_and_it_is_read()
    {
        var (state, _) = Game("golf", 2);
        var card = state.Definition.ScoreCard;

        Assert.NotNull(card);
        // Opens as a total, which fits in the corner at every seat count; the holes are
        // a tap away and may cover the table, because the player asked for them.
        Assert.Equal("total", card.View);
        Assert.True(card.Collapsible);
        Assert.Equal("H", card.RoundLabel);
        Assert.NotNull(card.Place);
    }

    [Fact]
    public void Every_round_scored_is_recorded_as_its_own_line()
    {
        var (state, _) = Game("hearts", 4);

        // Two rounds of made-up trick scores, through the engine's own scoring.
        for (int round = 1; round <= 2; round++)
        {
            state.RoundNumber = round;
            foreach (var p in state.Players)
                state.Metadata[$"tricks_taken:{p.Id}"] = "0";
            ScoringEngine.Apply(state);
        }

        Assert.Equal(2, state.ScoreHistory.Count);
        Assert.Equal([1, 2], state.ScoreHistory.Select(r => r.Round));

        // The totals and the rounds agree: a detail view that does not add up to the
        // total view is worse than showing neither.
        foreach (var p in state.Players)
            Assert.Equal(state.GetScore(p.Id), state.ScoreHistory.Sum(r => r.Scores.GetValueOrDefault(p.Id)));
    }

    [Fact]
    public void The_history_survives_a_save_and_restore()
    {
        var (state, _) = Game("hearts", 4);
        state.Metadata["tricks_taken:player0"] = "3";
        ScoringEngine.Apply(state);
        Assert.Single(state.ScoreHistory);

        var saved = GameStateSerializer.Snapshot(state, state.Players.Count, []);
        var (restored, restoredLogic) = Game("hearts", 4);
        GameStateSerializer.Restore(restored, restoredLogic, saved, restored.Players.Count, []);

        Assert.Equal(state.ScoreHistory.Count, restored.ScoreHistory.Count);
        Assert.Equal(state.ScoreHistory[0].Round, restored.ScoreHistory[0].Round);
        Assert.Equal(state.ScoreHistory[0].Scores, restored.ScoreHistory[0].Scores);
    }

    [Fact]
    public void A_score_card_without_a_place_is_a_definition_error()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        definition.ScoreCard!.Place = null;

        Assert.Contains(DefinitionValidator.Validate(definition), p => p.StartsWith("score_card:"));
    }

    [Fact]
    public void A_table_with_a_score_card_paints()
    {
        // The renderer draws it from the definition and the history; this is the check
        // that neither an empty history nor a full one throws.
        var (state, _) = Game("golf", 2);
        var renderer = new CardTableRenderer(new StubDriver()) { GameState = state };

        var info = new SKImageInfo(1000, 800);
        using var surface = SKSurface.Create(info);
        renderer.Paint(surface.Canvas, info);

        state.ScoreHistory.Add(new ScoreRound(1, new() { ["player0"] = 4, ["player1"] = 7 }));
        renderer.Paint(surface.Canvas, info);
    }
}
