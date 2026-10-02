using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// At a table of your own, seat 0 is you, by the name you saved — not "Player 1", which
/// read as a stranger sitting in your chair.
/// </summary>
public sealed class PlayerNameTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static GameTableViewModel Build(string? name)
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        return new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()))
        {
            TurnPace   = 0.001,
            PlayerName = name,
        };
    }

    [Fact]
    public async Task Seat_zero_takes_the_saved_name_and_the_computer_has_names_of_its_own()
    {
        var vm = Build("Brian");
        await vm.StartAsync("hearts", 4, resume: false, seed: 3);

        Assert.Equal("Brian", vm.State!.Players[0].Name);
        var bots = vm.State.Players.Skip(1).Select(p => p.Name).ToList();
        Assert.All(bots, n => Assert.Contains(n, BotNames.Pool));
        Assert.Equal(3, bots.Distinct().Count());
    }

    /// <summary>A computer seat never sits down under the name you play by.</summary>
    [Theory]
    [InlineData(1UL)] [InlineData(2UL)] [InlineData(3UL)] [InlineData(4UL)] [InlineData(5UL)]
    public async Task No_computer_seat_takes_your_name(ulong seed)
    {
        // A name in the pool, typed in lower case: five computer seats over five seeds
        // would draw it often if it were not kept out.
        var vm = Build("ana");
        await vm.StartAsync("go-fish", 6, resume: false, seed: seed);

        Assert.DoesNotContain(vm.State!.Players.Skip(1), p => p.Name.Equals("Ana", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_seeded_table_names_the_computer_the_same_way_every_time()
    {
        var a = Build("Brian"); await a.StartAsync("hearts", 4, resume: false, seed: 9);
        var b = Build("Brian"); await b.StartAsync("hearts", 4, resume: false, seed: 9);

        Assert.Equal(a.State!.Players.Select(p => p.Name), b.State!.Players.Select(p => p.Name));
    }

    /// <summary>
    /// A resumed game keeps the names it was played under — its log already says "Mae
    /// drew a card", and Mae must still be at the table to have drawn it.
    /// </summary>
    [Fact]
    public void A_saved_game_keeps_everyones_names()
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), "hearts")!;
        var played = new GameState { GameId = definition.Id, Definition = definition, BotNames = ["Mae", "Ezra", "Juno"] };
        var logic  = LogicRegistry.Create(definition);
        logic.Initialize(played, 4, []);

        var saved    = GameStateSerializer.Snapshot(played, 4, []);
        var resumed  = new GameState { GameId = definition.Id, Definition = definition };
        GameStateSerializer.Restore(resumed, LogicRegistry.Create(definition), saved, 4, []);

        Assert.Equal(["Player 1", "Mae", "Ezra", "Juno"], resumed.Players.Select(p => p.Name));
    }

    [Fact]
    public async Task Without_a_name_seat_zero_is_player_one()
    {
        var vm = Build(null);
        await vm.StartAsync("hearts", 4, resume: false, seed: 3);

        Assert.Equal("Player 1", vm.State!.Players[0].Name);
    }
}
