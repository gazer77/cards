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
    public async Task Seat_zero_takes_the_saved_name_and_the_computer_keeps_its_own()
    {
        var vm = Build("Brian");
        await vm.StartAsync("hearts", 4, resume: false, seed: 3);

        Assert.Equal("Brian", vm.State!.Players[0].Name);
        Assert.Equal(["Bot 1", "Bot 2", "Bot 3"], vm.State.Players.Skip(1).Select(p => p.Name));
    }

    [Fact]
    public async Task Without_a_name_seat_zero_is_player_one()
    {
        var vm = Build(null);
        await vm.StartAsync("hearts", 4, resume: false, seed: 3);

        Assert.Equal("Player 1", vm.State!.Players[0].Name);
    }
}
