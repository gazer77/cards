using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Saving and reloading must not change the cards in play.
///
/// The browser client saves to localStorage and restores on every visit, so a player
/// reloading mid-game takes this path constantly — and it is the one path the
/// engine-level tests never touch, because they hold a single state in memory for a
/// whole game.
/// </summary>
public sealed class SaveRestoreConservationTests
{
    /// <summary>A save store that keeps the written value, like localStorage does.</summary>
    private sealed class MemorySaveStore : ISaveStore
    {
        private readonly Dictionary<string, string> _items = [];
        public bool Exists(string key) => _items.ContainsKey(key);
        public void Delete(string key) => _items.Remove(key);
        public Task WriteAsync(string key, string contents) { _items[key] = contents; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string key) => Task.FromResult(_items.GetValueOrDefault(key));
    }

    private static Dictionary<Rank, int> Census(GameState s) =>
        s.Zones.Values.SelectMany(z => z.Cards)
            .GroupBy(c => c.Rank)
            .ToDictionary(g => g.Key, g => g.Count());

    private static int Total(GameState s) => s.Zones.Values.Sum(z => z.Count);

    [Theory]
    [InlineData("go-fish")]
    [InlineData("gin-rummy")]
    [InlineData("hearts")]
    public async Task A_save_and_reload_keeps_every_card(string gameId)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var store = new MemorySaveStore();
        var saves = new GameSaveService(store);

        var vm = new GameTableViewModel(loader, saves);
        await vm.StartAsync(gameId, 2, resume: false, seed: 7);

        // Play a little, the way the table does.
        for (int i = 0; i < 12 && !vm.IsGameOver; i++)
        {
            var selectable = vm.SelectableCardIds;
            if (vm.SelectedCardId is null && selectable.Count > 0)
                await vm.TapCard(selectable[0]);
            else if (vm.Actions.Count > 0)
                await vm.Invoke(vm.Actions[0]);
            else if (selectable.Count > 0)
                await vm.TapCard(selectable[0]);
            else break;
        }

        var before = Census(vm.State!);
        int beforeTotal = Total(vm.State!);

        await vm.SaveAsync();

        // A fresh view model, as after a browser reload.
        var reloaded = new GameTableViewModel(loader, saves);
        await reloaded.StartAsync(gameId, 2, resume: true, seed: 7);

        Assert.Equal(beforeTotal, Total(reloaded.State!));
        Assert.Equal(before, Census(reloaded.State!));
    }

    /// <summary>
    /// Repeated reloads must not compound. A single round trip losing nothing is not
    /// the same as ten in a row losing nothing, and a player reloads many times.
    /// </summary>
    [Fact]
    public async Task Reloading_repeatedly_keeps_every_card()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var saves = new GameSaveService(new MemorySaveStore());

        var vm = new GameTableViewModel(loader, saves);
        await vm.StartAsync("go-fish", 2, resume: false, seed: 3);

        for (int round = 0; round < 10; round++)
        {
            for (int i = 0; i < 4 && !vm.IsGameOver; i++)
            {
                var selectable = vm.SelectableCardIds;
                if (vm.SelectedCardId is null && selectable.Count > 0)
                    await vm.TapCard(selectable[0]);
                else if (vm.Actions.Count > 0)
                    await vm.Invoke(vm.Actions[0]);
                else break;
            }

            await vm.SaveAsync();

            vm = new GameTableViewModel(loader, saves);
            await vm.StartAsync("go-fish", 2, resume: true, seed: 3);

            Assert.True(Total(vm.State!) == 52,
                $"After {round + 1} reloads the game holds {Total(vm.State!)} cards, not 52.");

            foreach (var (rank, count) in Census(vm.State!))
                Assert.True(count == 4,
                    $"After {round + 1} reloads there are {count} {rank}s, not 4.");
        }
    }
}
