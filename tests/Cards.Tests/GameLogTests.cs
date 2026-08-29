using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Covers the game log and the messages that feed it.
///
/// Nothing in the engine writes to GameState.GameLog — it exposes a status line
/// describing the position, and watching that line for changes is what turns it into a
/// history. That producer lived in the MAUI page, so the browser client had a log view
/// with nothing behind it. These pin the behaviour to the shared view model so it
/// cannot go missing from a client again.
/// </summary>
public sealed class GameLogTests
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
        return new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()));
    }

    [Fact]
    public async Task Play_produces_log_entries()
    {
        var vm = Build();
        await vm.StartAsync("war", 2, resume: false, seed: 5);

        for (int i = 0; i < 6 && !vm.IsGameOver; i++)
            await vm.TapTable();

        Assert.NotEmpty(vm.GameLog);
    }

    [Fact]
    public async Task Messages_are_attributed_to_a_real_seat()
    {
        var vm = Build();
        var seats = new List<string>();
        vm.MessagePosted += (playerId, _) => seats.Add(playerId);

        await vm.StartAsync("war", 2, resume: false, seed: 5);
        for (int i = 0; i < 6 && !vm.IsGameOver; i++)
            await vm.TapTable();

        Assert.NotEmpty(seats);

        // A bubble is drawn against the named seat's zone, so an id that matches no
        // player renders nothing at all — silently, and only in some games.
        var known = vm.State!.Players.Select(p => p.Id).ToHashSet();
        Assert.All(seats, s => Assert.Contains(s, known));
    }

    [Fact]
    public async Task An_unchanged_position_is_not_logged_twice_in_a_row()
    {
        var vm = Build();
        await vm.StartAsync("war", 2, resume: false, seed: 5);

        for (int i = 0; i < 10 && !vm.IsGameOver; i++)
            await vm.TapTable();

        // The status line describes the position, not the move, so it often reads the
        // same twice running. Logging it every time would bury real events.
        for (int i = 1; i < vm.GameLog.Count; i++)
            Assert.NotEqual(vm.GameLog[i - 1], vm.GameLog[i]);
    }

    [Fact]
    public async Task Every_logged_line_says_something()
    {
        var vm = Build();
        await vm.StartAsync("gin-rummy", 2, resume: false, seed: 5);

        Assert.All(vm.GameLog, line => Assert.False(string.IsNullOrWhiteSpace(line)));
    }
}
