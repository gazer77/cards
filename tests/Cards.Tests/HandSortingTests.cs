using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Covers the hand-sorting offered by the in-game menu.
///
/// The rules here are per-game and easy to get subtly wrong in a way nobody notices:
/// offering a sort a game never intended, burying its preferred one, or quietly
/// rearranging a hand the player is not supposed to see.
/// </summary>
public sealed class HandSortingTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static async Task<GameTableViewModel> Start(string gameId, int seats)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()));

        await vm.StartAsync(gameId, seats, resume: false, seed: 11);
        return vm;
    }

    [Fact]
    public async Task Offers_only_the_sorts_a_game_declares()
    {
        // gin-rummy declares three of the four modes.
        var vm = await Start("gin-rummy", 2);
        var modes = vm.SortModes.Select(m => m.Mode).ToList();

        Assert.DoesNotContain("rank", modes);   // declared modes exclude ace-low
        Assert.Contains("rank_ace_high", modes);
        Assert.Contains("suit_value", modes);
    }

    [Fact]
    public async Task Puts_the_games_preferred_sort_first()
    {
        var vm = await Start("gin-rummy", 2);

        // The definition's default_sort is rank_ace_high; a player opening the menu
        // should find it at the top rather than hunting for it.
        Assert.Equal("rank_ace_high", vm.SortModes[0].Mode);
    }

    [Fact]
    public async Task Always_offers_manual_arrangement_last()
    {
        var vm = await Start("gin-rummy", 2);

        Assert.Equal(GameTableViewModel.CustomSortMode, vm.SortModes[^1].Mode);
    }

    [Fact]
    public async Task Sorting_reorders_a_hand_the_player_can_see()
    {
        var vm = await Start("gin-rummy", 2);

        var hand = vm.State!.Zones.Values.First(z => z.Type == "hand" && z.Visibility == "owner");
        var before = hand.Cards.Select(c => c.Id).ToList();

        vm.SortHand("rank_ace_high");
        var after = hand.Cards.Select(c => c.Id).ToList();

        // Same cards, and now in rank order.
        Assert.Equal(before.OrderBy(x => x), after.OrderBy(x => x));

        var ranks = hand.Cards.Select(c => c.Rank == Rank.Ace ? 14 : (int)c.Rank).ToList();
        Assert.Equal(ranks.OrderBy(r => r), ranks);
    }

    [Fact]
    public async Task Leaves_hands_the_player_cannot_see_alone()
    {
        var vm = await Start("gin-rummy", 2);

        var hidden = vm.State!.Zones.Values
            .Where(z => z.Type == "hand" && z.Visibility is not ("owner" or "all"))
            .ToList();

        var before = hidden.Select(z => z.Cards.Select(c => c.Id).ToList()).ToList();

        vm.SortHand("rank_ace_high");

        // Sorting is a convenience for looking at your own cards. Reaching into a
        // hand nobody can see does nothing visible and rearranges an opponent's.
        for (int i = 0; i < hidden.Count; i++)
            Assert.Equal(before[i], hidden[i].Cards.Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task Manual_arrangement_is_not_a_sort()
    {
        var vm = await Start("gin-rummy", 2);

        var hand = vm.State!.Zones.Values.First(z => z.Type == "hand" && z.Visibility == "owner");
        var before = hand.Cards.Select(c => c.Id).ToList();

        vm.SortHand(GameTableViewModel.CustomSortMode);

        Assert.Equal(before, hand.Cards.Select(c => c.Id).ToList());
    }

    [Fact]
    public async Task Games_with_no_visible_hand_offer_no_sorting()
    {
        // War deals face-down piles; there is nothing for the player to arrange.
        var vm = await Start("war", 2);

        Assert.False(vm.CanSortHand);
    }

    /// <summary>
    /// A chosen sort stays in effect as cards arrive.
    ///
    /// Sorting once on request is not what choosing a sort means to a player: a card
    /// won from an opponent is appended to the hand, so an ordered hand comes apart
    /// over a game unless new cards land where they belong.
    /// </summary>
    [Fact]
    public async Task A_chosen_sort_keeps_new_cards_in_order()
    {
        var vm = await Start("go-fish", 2);
        vm.SortHand("rank_ace_high");

        var hand = vm.State!.Zones.Values.First(z => z.Type == "hand" && z.Visibility == "owner");

        for (int i = 0; i < 30 && !vm.IsGameOver; i++)
        {
            var selectable = vm.SelectableCardIds;
            var ask = vm.Actions.FirstOrDefault(a => a.Type == "ask");

            if (ask is not null)                                    await vm.Invoke(ask);
            else if (vm.SelectedCardId is null && selectable.Count > 0) await vm.TapCard(selectable[0]);
            else if (vm.Actions.Count > 0)                          await vm.Invoke(vm.Actions[0]);
            else break;

            var ranks = hand.Cards.Select(c => c.Rank == Rank.Ace ? 14 : (int)c.Rank).ToList();
            Assert.True(ranks.SequenceEqual(ranks.OrderBy(r => r)),
                $"Hand fell out of order after {i + 1} moves: " +
                string.Join(" ", hand.Cards.Select(c => c.Id)));
        }
    }

    /// <summary>
    /// Free must stop the sorting, not merely skip one pass — otherwise a hand the
    /// player arranged by dragging is quietly reordered on their next turn.
    /// </summary>
    [Fact]
    public async Task Free_stops_the_hand_being_reordered()
    {
        var vm = await Start("go-fish", 2);
        vm.SortHand("rank_ace_high");
        vm.SortHand(GameTableViewModel.CustomSortMode);

        var hand = vm.State!.Zones.Values.First(z => z.Type == "hand" && z.Visibility == "owner");

        // Shuffle the hand into a deliberately wrong order and confirm it survives play.
        var scrambled = hand.Cards.OrderBy(c => c.Suit).ThenByDescending(c => c.Rank).ToList();
        hand.Reorder(scrambled);
        var expected = scrambled.Select(c => c.Id).ToList();

        var selectable = vm.SelectableCardIds;
        if (selectable.Count > 0) await vm.TapCard(selectable[0]);

        Assert.Equal(expected, hand.Cards.Select(c => c.Id).ToList());
    }
}
