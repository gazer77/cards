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

    /// <summary>
    /// A save written at one table size must not be loaded into another.
    ///
    /// Restore replaces the zones wholesale, so a four-player save loaded into a
    /// two-player game leaves hand:player2 and hand:player3 in place — holding real
    /// cards that no handler will ever address. The totals still add up, so nothing
    /// looks wrong; the game simply cannot be finished, because the ranks in those
    /// hands can never be completed. This is how a game that once allowed four players
    /// reached a player who had since only ever opened two-player tables.
    /// </summary>
    [Fact]
    public async Task A_save_from_a_bigger_table_is_not_loaded_into_a_smaller_one()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var saves = new GameSaveService(new MemorySaveStore());

        // hearts still allows four; go-fish no longer does, so this is written the way
        // an old save would have been.
        var four = new GameTableViewModel(loader, saves);
        await four.StartAsync("hearts", 4, resume: false, seed: 5);
        await four.SaveAsync();

        var two = new GameTableViewModel(loader, saves);
        await two.StartAsync("hearts", 2, resume: true, seed: 5);

        var orphans = GameStateSerializer.OrphanedZones(two.State!);
        Assert.True(orphans.Count == 0,
            $"Loaded a 4-player save into a 2-player game, leaving unreachable zones: " +
            string.Join(", ", orphans));

        // And every zone that exists belongs to a seat that is actually playing.
        foreach (var (id, zone) in two.State!.Zones)
            if (zone.OwnerId is not null)
                Assert.Contains(zone.OwnerId, two.State.Players.Select(p => p.Id));
    }

    [Fact]
    public async Task A_save_from_the_same_table_size_still_loads()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var saves = new GameSaveService(new MemorySaveStore());

        var first = new GameTableViewModel(loader, saves);
        await first.StartAsync("hearts", 4, resume: false, seed: 5);

        // Make the saved position distinguishable from a fresh deal.
        first.SortHand("rank");
        await first.SaveAsync();
        var expected = Census(first.State!);

        var resumed = new GameTableViewModel(loader, saves);
        await resumed.StartAsync("hearts", 4, resume: true, seed: 5);

        // Refusing mismatched saves must not throw out matching ones as well.
        Assert.Equal(expected, Census(resumed.State!));
        Assert.Equal(4, resumed.State!.Players.Count);
    }

    /// <summary>
    /// Every card must be reachable, not merely present. A card in an unreachable zone
    /// counts toward the deck while being out of the game, which is exactly why the
    /// totals looked right while the game could not be finished.
    /// </summary>
    [Fact]
    public async Task Every_card_belongs_to_a_seat_that_is_playing()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var saves = new GameSaveService(new MemorySaveStore());

        foreach (var (gameId, seats) in new[] { ("go-fish", 2), ("hearts", 4), ("gin-rummy", 2) })
        {
            var vm = new GameTableViewModel(loader, saves);
            await vm.StartAsync(gameId, seats, resume: false, seed: 9);
            await vm.SaveAsync();

            var resumed = new GameTableViewModel(loader, saves);
            await resumed.StartAsync(gameId, seats, resume: true, seed: 9);

            Assert.True(GameStateSerializer.OrphanedZones(resumed.State!).Count == 0,
                $"{gameId} at {seats} seats restored with unreachable zones.");
        }
    }
}
