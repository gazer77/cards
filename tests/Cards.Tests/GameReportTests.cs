using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// The exported report has to be enough to act on without a follow-up question.
///
/// Each of these pins something that a real bug report in this project turned out to
/// need: where every card is, whose turn it is, what the engine believes privately,
/// and the log.
/// </summary>
public sealed class GameReportTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static async Task<GameTableViewModel> Played()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()))
        {
            TurnPace = 0.001,
        };

        await vm.StartAsync("go-fish", 2, resume: false, seed: 7);

        for (int i = 0; i < 10 && !vm.IsGameOver; i++)
        {
            var ask = vm.Actions.FirstOrDefault(a => a.Type == "ask");
            if (ask is not null) { await vm.Invoke(ask); continue; }

            var selectable = vm.SelectableCardIds;
            if (vm.SelectedCardId is null && selectable.Count > 0) await vm.TapCard(selectable[0]);
            else if (vm.Actions.Count > 0) await vm.Invoke(vm.Actions[0]);
            else break;
        }

        return vm;
    }

    [Fact]
    public async Task Accounts_for_every_card_in_the_deck()
    {
        var vm = await Played();
        var report = GameReport.Build(vm, 2, []);

        // Counting cards from a screenshot is unreliable, which is the whole reason
        // this exists: the report must name every card and the zone holding it.
        foreach (var card in vm.State!.Zones.Values.SelectMany(z => z.Cards))
            Assert.Contains(card.Id, report);

        foreach (var zoneId in vm.State.Zones.Keys)
            Assert.Contains(zoneId, report);
    }

    [Fact]
    public async Task Flags_ranks_that_do_not_add_up()
    {
        var vm = await Played();

        Assert.Contains("all complete", GameReport.Build(vm, 2, []));

        // Palm a card and the report must say so, rather than reading as healthy.
        var hand = vm.State!.Zones.Values.First(z => z.Count > 0);
        hand.Remove(hand.Cards[0]);

        Assert.Contains("UNEVEN", GameReport.Build(vm, 2, []));
    }

    [Fact]
    public async Task Includes_the_log_the_turn_and_the_engines_own_bookkeeping()
    {
        var vm = await Played();
        var report = GameReport.Build(vm, 2, ["pairs"]);

        Assert.Contains("--- log", report);
        Assert.Contains(vm.State!.CurrentPlayer.Id, report);
        Assert.Contains("pairs", report);

        // gf_state is how the Go Fish handler tracks whose turn it is; an odd decision
        // usually makes sense only once you can see values like it.
        Assert.Contains("gf_state", report);
    }

    [Fact]
    public async Task Survives_a_game_that_never_started()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()));

        // Exporting from a table that failed to load is exactly when a report is most
        // wanted, so it must not throw.
        var report = GameReport.Build(vm, 2, []);
        Assert.Contains("no game loaded", report);
    }

    [Fact]
    public async Task Names_the_file_after_the_game_and_the_time()
    {
        var vm = await Played();
        var name = GameReport.FileName(vm);

        Assert.StartsWith("cards-go-fish-", name);
        Assert.EndsWith(".txt", name);
    }
}
