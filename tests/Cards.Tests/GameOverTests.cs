using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// What the table shows once a game is over.
///
/// Worth testing because the obvious implementation is wrong: not every game is won by
/// the highest score. Hearts and Golf are won by the lowest, and Hand and Foot can leave
/// every player deeply negative. Sorting descending would put the loser at the top of
/// half the catalogue.
/// </summary>
public sealed class GameOverTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static GameTableViewModel Build()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        return new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()))
        {
            TurnPace = 0.001,
        };
    }

    private static async Task<GameTableViewModel> PlayToEnd(string gameId, int seats, ulong seed)
    {
        var vm = Build();
        await vm.StartAsync(gameId, seats, resume: false, seed: seed);

        // TableDriver, not a loop written here. Four earlier attempts at an ad-hoc
        // driver each measured themselves instead of the game, and a fifth in this file
        // left Hearts unfinished with no scores to assert against.
        int tap = 0;
        for (int i = 0; i < 4000 && !vm.IsGameOver; i++)
        {
            var step = TableDriver.Step(vm.State!, vm.Logic!, ref tap);
            if (step != TableDriver.StepResult.Moved) break;
        }

        return vm;
    }

    [Theory]
    [InlineData("war", 2)]
    [InlineData("go-fish", 2)]
    [InlineData("blackjack", 2)]
    public async Task A_finished_game_has_something_to_show(string gameId, int seats)
    {
        var vm = await PlayToEnd(gameId, seats, 7);

        Assert.True(vm.IsGameOver, $"{gameId} did not finish.");

        // The heading is the engine's own closing line; an empty one would leave the
        // player looking at a blank panel.
        Assert.False(string.IsNullOrWhiteSpace(vm.StatusText));
    }

    /// <summary>
    /// Hearts rather than Blackjack: Blackjack keeps no scores at all, so a standings
    /// assertion against it passes without testing anything. The first version of this
    /// did exactly that.
    /// </summary>
    [Fact]
    public async Task Standings_name_every_seat_that_scored()
    {
        var vm = await PlayToEnd("hearts", 4, 7);

        var scores = vm.FinalScores;
        Assert.NotEmpty(scores);

        foreach (var player in vm.State!.Players.Where(p => vm.State.Scores.ContainsKey(p.Id)))
            Assert.Contains(scores, s => s.Name == player.Name);
    }

    /// <summary>
    /// The winner belongs at the top whichever direction the game scores in. Hearts is
    /// the case that matters: it is won by the lowest score, so ordering by highest
    /// would list the winner last.
    /// </summary>
    [Fact]
    public async Task The_declared_winner_is_listed_first()
    {
        var vm = await PlayToEnd("hearts", 4, 7);

        var scores = vm.FinalScores;
        Assert.NotEmpty(scores);
        Assert.Contains(scores, s => s.IsWinner);

        Assert.True(scores[0].IsWinner,
            "Winner is not at the top: " +
            string.Join(", ", scores.Select(s => $"{s.Name}={s.Score}{(s.IsWinner ? "*" : "")}")));
    }

    [Fact]
    public async Task An_unfinished_game_shows_no_result()
    {
        var vm = Build();
        await vm.StartAsync("gin-rummy", 2, resume: false, seed: 7);

        // The overlay is gated on this, so a false positive would cover a live table
        // with a result screen.
        Assert.False(vm.IsGameOver);
    }

    [Fact]
    public async Task The_detail_line_says_something_or_nothing_at_all()
    {
        var vm = await PlayToEnd("war", 2, 7);

        // Never whitespace: the panel hides the line when it is empty, and " " would
        // render as a blank gap instead.
        var detail = vm.GameOverDetail;
        Assert.True(detail.Length == 0 || detail.Trim().Length > 0);
    }
}
