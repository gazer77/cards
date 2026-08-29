using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Plays Go Fish through the view model, the way the table does.
///
/// The engine-level tests call IGameLogic directly and so never exercise the client's
/// turn loop — the part that decides when the AI gets to move. A game that is correct
/// in the engine can still sit forever on "AI's turn" if that loop stops driving it.
/// </summary>
public sealed class GoFishTurnLoopTests
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
            // The real client paces turns for readability; a test only needs the
            // ordering, not the waiting.
            TurnPace = 0.001,
        };
    }

    [Theory]
    [InlineData(3UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    public async Task A_game_played_through_the_table_reaches_an_end(ulong seed)
    {
        var vm = Build();
        await vm.StartAsync("go-fish", 2, resume: false, seed: seed);

        int moves = 0;
        while (!vm.IsGameOver && moves < 600)
        {
            // Exactly the affordances the table offers: tap a card, then press a button.
            var ask = vm.Actions.FirstOrDefault(a => a.Type == "ask");
            if (ask is not null)                       { await vm.Invoke(ask); moves++; continue; }

            var selectable = vm.SelectableCardIds;
            if (vm.SelectedCardId is null && selectable.Count > 0)
                                                       { await vm.TapCard(selectable[0]); moves++; continue; }

            if (vm.Actions.Count > 0)                  { await vm.Invoke(vm.Actions[0]); moves++; continue; }

            // Nothing to tap and nothing to press, with the game not over: the table is
            // stuck and the player has no way to continue.
            Assert.Fail(
                $"No move available after {moves} moves. Status: {vm.StatusText} | " +
                $"deck {vm.State!.Zones["deck"].Count}, " +
                string.Join(", ", vm.State.Zones
                    .Where(z => z.Key.StartsWith("hand:") || z.Key.StartsWith("books:"))
                    .Select(z => $"{z.Key}={z.Value.Count}")));
        }

        Assert.True(vm.IsGameOver, $"Game had not finished after {moves} moves: {vm.StatusText}");
    }
}
