using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Saving and resuming must preserve the position exactly, and a save must only ever be
/// loaded back into the table it was written at.
///
/// The browser saves to localStorage and resumes from a list, so a player takes this
/// path constantly — and it is where a class of bug lives that in-memory play cannot
/// reach, because those tests hold one state at one table size for a whole game.
/// </summary>
public sealed class SaveRestoreConservationTests
{
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

    /// <summary>Card ids by zone — the position itself, not just the totals.</summary>
    private static Dictionary<string, List<string>> Layout(GameState s) =>
        s.Zones.ToDictionary(z => z.Key, z => z.Value.Cards.Select(c => c.Id).ToList());

    private static int Total(GameState s) => s.Zones.Values.Sum(z => z.Count);

    private static (GameLoader Loader, GameSaveService Saves) Fresh()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        return (loader, new GameSaveService(new MemorySaveStore()));
    }

    private static GameTableViewModel Vm(GameLoader loader, GameSaveService saves)
        => new(loader, saves) { TurnPace = 0.001 };

    private static async Task Play(GameTableViewModel vm, int moves)
    {
        for (int i = 0; i < moves && !vm.IsGameOver; i++)
        {
            var ask = vm.Actions.FirstOrDefault(a => a.Type == "ask");
            if (ask is not null) { await vm.Invoke(ask); continue; }

            var selectable = vm.SelectableCardIds;
            if (vm.SelectedCardId is null && selectable.Count > 0) await vm.TapCard(selectable[0]);
            else if (vm.Actions.Count > 0) await vm.Invoke(vm.Actions[0]);
            else break;
        }
    }

    [Theory]
    [InlineData("go-fish", 2)]
    [InlineData("gin-rummy", 2)]
    [InlineData("hearts", 4)]
    public async Task Resuming_restores_the_position_that_was_saved(string gameId, int seats)
    {
        var (loader, saves) = Fresh();

        var vm = Vm(loader, saves);
        await vm.StartAsync(gameId, seats, resume: false, seed: 7);
        await Play(vm, 8);

        var expected = Layout(vm.State!);
        await vm.SaveAsync();
        Assert.NotNull(vm.SlotId);

        var resumed = Vm(loader, saves);
        await resumed.StartAsync(gameId, seats, resume: true, resumeSlotId: vm.SlotId);

        // Every card back in the zone it came from. Comparing totals alone would pass
        // on a fresh deal, which is exactly what a broken resume produces.
        Assert.Equal(expected, Layout(resumed.State!));
    }

    [Fact]
    public async Task Repeated_save_and_resume_cycles_lose_nothing()
    {
        var (loader, saves) = Fresh();

        var vm = Vm(loader, saves);
        await vm.StartAsync("go-fish", 2, resume: false, seed: 3);

        for (int round = 0; round < 10; round++)
        {
            await Play(vm, 4);
            await vm.SaveAsync();
            var slot = vm.SlotId;

            vm = Vm(loader, saves);
            await vm.StartAsync("go-fish", 2, resume: true, resumeSlotId: slot);

            Assert.True(Total(vm.State!) == 52,
                $"After {round + 1} cycles the game holds {Total(vm.State!)} cards, not 52.");

            foreach (var (rank, count) in Census(vm.State!))
                Assert.True(count == 4, $"After {round + 1} cycles there are {count} {rank}s.");
        }

        // Ten cycles must update one entry, not leave ten in the resume list.
        Assert.Single(saves.SavesFor("go-fish"));
    }

    /// <summary>
    /// A save carries its own table. Resuming keeps the seat count it was written at
    /// whatever the caller asks for — otherwise a four-player save loaded into a
    /// two-player game keeps hands for seats that no longer exist, holding cards nobody
    /// can reach while every total still adds up.
    /// </summary>
    [Fact]
    public async Task A_resumed_game_keeps_the_seat_count_it_was_saved_at()
    {
        var (loader, saves) = Fresh();

        var four = Vm(loader, saves);
        await four.StartAsync("hearts", 4, resume: false, seed: 5);
        await four.SaveAsync();

        var resumed = Vm(loader, saves);
        await resumed.StartAsync("hearts", 2, resume: true, resumeSlotId: four.SlotId);

        Assert.Equal(4, resumed.State!.Players.Count);
        Assert.Empty(GameStateSerializer.OrphanedZones(resumed.State));
    }

    [Fact]
    public async Task Every_card_belongs_to_a_seat_that_is_playing()
    {
        var (loader, saves) = Fresh();

        foreach (var (gameId, seats) in new[] { ("go-fish", 2), ("hearts", 4), ("gin-rummy", 2) })
        {
            var vm = Vm(loader, saves);
            await vm.StartAsync(gameId, seats, resume: false, seed: 9);
            await vm.SaveAsync();

            var resumed = Vm(loader, saves);
            await resumed.StartAsync(gameId, seats, resume: true, resumeSlotId: vm.SlotId);

            Assert.True(GameStateSerializer.OrphanedZones(resumed.State!).Count == 0,
                $"{gameId} at {seats} seats restored with unreachable zones.");
        }
    }

    /// <summary>
    /// Keeping several games at once is the point of slots: a four-player Hearts and a
    /// two-player Hearts used to compete for one entry, which is how a save came to be
    /// loaded at the wrong size.
    /// </summary>
    [Fact]
    public async Task Games_of_the_same_title_at_different_sizes_both_survive()
    {
        var (loader, saves) = Fresh();

        var four = Vm(loader, saves);
        await four.StartAsync("hearts", 4, resume: false, seed: 5);
        await four.SaveAsync();

        var two = Vm(loader, saves);
        await two.StartAsync("hearts", 2, resume: false, seed: 6);
        await two.SaveAsync();

        var slots = saves.SavesFor("hearts");
        Assert.Equal(2, slots.Count);
        Assert.Contains(slots, s => s.PlayerCount == 4);
        Assert.Contains(slots, s => s.PlayerCount == 2);

        var back = Vm(loader, saves);
        await back.StartAsync("hearts", 4, resume: true, resumeSlotId: two.SlotId);
        Assert.Equal(2, back.State!.Players.Count);
    }

    [Fact]
    public async Task Saves_from_different_games_are_listed_newest_first()
    {
        var (loader, saves) = Fresh();

        foreach (var gameId in new[] { "go-fish", "hearts", "gin-rummy" })
        {
            var vm = Vm(loader, saves);
            await vm.StartAsync(gameId, gameId == "hearts" ? 4 : 2, resume: false, seed: 2);
            await vm.SaveAsync();
        }

        Assert.Equal(3, saves.Saves.Count);
        Assert.Single(saves.SavesFor("hearts"));

        // Newest first, so the resume list opens on the game just left.
        var order = saves.Saves.Select(s => s.SavedAt).ToList();
        Assert.Equal(order.OrderByDescending(t => t), order);
    }

    [Fact]
    public async Task A_save_describes_itself_without_being_loaded()
    {
        var (loader, saves) = Fresh();

        var vm = Vm(loader, saves);
        await vm.StartAsync("hearts", 4, resume: false, seed: 5);
        await vm.SaveAsync();

        var slot = saves.SavesFor("hearts").Single();

        // The resume list renders from this alone, so it has to carry enough to choose
        // between saves without deserialising every game.
        Assert.Equal("Hearts", slot.GameName);
        Assert.Equal(4, slot.PlayerCount);
        Assert.NotEmpty(slot.Summary);
    }

    [Fact]
    public async Task Deleting_a_save_removes_it_from_the_list_and_from_storage()
    {
        var (loader, saves) = Fresh();

        var vm = Vm(loader, saves);
        await vm.StartAsync("go-fish", 2, resume: false, seed: 4);
        await vm.SaveAsync();
        var slot = vm.SlotId!;

        await vm.DeleteSaveAsync();

        Assert.Empty(saves.SavesFor("go-fish"));
        Assert.Null(saves.FindSlot(slot));

        // Resuming it now falls back to a fresh game rather than failing.
        var after = Vm(loader, saves);
        Assert.True(await after.StartAsync("go-fish", 2, resume: true, resumeSlotId: slot));
        Assert.Equal(52, Total(after.State!));
    }

    [Fact]
    public async Task A_missing_slot_starts_a_fresh_game_rather_than_failing()
    {
        var (loader, saves) = Fresh();

        var vm = Vm(loader, saves);
        Assert.True(await vm.StartAsync("go-fish", 2, resume: true, resumeSlotId: "does-not-exist"));
        Assert.Equal(52, Total(vm.State!));
    }
}
