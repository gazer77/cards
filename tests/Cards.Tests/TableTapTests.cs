using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// A tap on bare felt.
///
/// The bug as reported: "I clicked on an opponent's card and it discarded my drawn
/// card." A zone drawn turned — every seat but your own — records no card rectangles,
/// so a click on an opponent's card is a click on nothing, and the felt fired the one
/// action available, which was Discard Drawn.
/// </summary>
public sealed class TableTapTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static GameTableViewModel Vm()
        => new(new GameLoader(new EmbeddedGameAssetSource()), new GameSaveService(new NoSaveStore()));

    [Fact]
    public async Task Felt_does_not_fire_an_action_that_has_a_button()
    {
        var vm = Vm();
        await vm.StartAsync("golf", 2, resume: false, seed: 11);

        // Through the peek to a turn holding a drawn card.
        while (vm.State!.CurrentPhaseId == "peek")
        {
            var grid = vm.State.Zones[$"grid:{vm.State.CurrentPlayer.Id}"];
            var card = grid.Cards.First(c => !c.IsFaceUp && vm.SelectableCardIds.Contains(c.Id));
            await vm.TapCard(card.Id, card.Uid);
            if (vm.Actions.FirstOrDefault(a => a.Type == "flip") is { } flip) await vm.Invoke(flip);
        }

        await vm.Invoke(vm.Actions.First(a => a.Type == "draw_from_deck"));
        var drawn = vm.State.Zones[$"hand:{vm.State.CurrentPlayer.Id}"].Cards.Single();

        // Exactly the situation: one action, and it is a labelled one.
        var only = Assert.Single(vm.Actions);
        Assert.Equal("discard_drawn", only.Type);
        Assert.NotNull(only.Label);

        await vm.TapTable();

        Assert.Contains(vm.State.Zones[$"hand:{vm.State.CurrentPlayer.Id}"].Cards, c => c.Uid == drawn.Uid);
    }

    [Fact]
    public async Task Felt_still_carries_a_game_whose_only_affordance_is_the_table()
    {
        // War is played by tapping the table; its action has no label and no button,
        // so the felt is the only way to play it at all.
        var vm = Vm();
        await vm.StartAsync("war", 2, resume: false, seed: 3);

        // Asked of the engine, not the button bar: the bar shows only what can be
        // pressed, and this action is precisely the one that cannot be.
        var action = Assert.Single(vm.Logic!.GetValidActions(vm.State!));
        Assert.Null(action.Label);
        Assert.Empty(vm.Actions);

        string before = vm.State!.CurrentPhaseId;
        await vm.TapTable();
        Assert.True(vm.State.CurrentPhaseId != before
                    || vm.State.Zones.Values.Any(z => z.Cards.Any(c => c.IsFaceUp)));
    }
}
