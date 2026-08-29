using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Card.Uid identifies a physical card; Card.Id describes one.
///
/// The distinction only bites in multi-deck games, where several cards answer to the
/// same description. Everything that tracks a card as an object — hit-testing, every
/// animation, and masked multiplayer, where a hidden card has no rank or suit to name
/// it by — needs the former. The rules want the latter, because any five of hearts
/// plays like any other.
/// </summary>
public sealed class CardIdentityTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    [Theory]
    [InlineData("standard-52", 52)]
    [InlineData("standard-104", 104)]
    [InlineData("standard-52-jokers", 54)]
    [InlineData("euchre-24", 24)]
    [InlineData("pinochle-48", 48)]
    public void Every_card_in_a_deck_has_its_own_uid(string deckType, int expectedSize)
    {
        var deck = DeckBuilder.Build(deckType);

        Assert.Equal(expectedSize, deck.Count);
        Assert.Equal(deck.Count, deck.Select(c => c.Uid).Distinct().Count());
        Assert.DoesNotContain(deck, c => c.Uid == 0);
    }

    /// <summary>
    /// The decks where this matters: two cards that are the same card, and must still
    /// be told apart. Before uids, a pinochle table had 24 pairs the renderer could not
    /// distinguish, so a tap could move the wrong one.
    /// </summary>
    [Theory]
    [InlineData("standard-104")]
    [InlineData("pinochle-48")]
    public void Duplicate_cards_share_a_description_but_not_an_identity(string deckType)
    {
        var deck = DeckBuilder.Build(deckType);

        var duplicated = deck.GroupBy(c => c.Id).Where(g => g.Count() > 1).ToList();
        Assert.NotEmpty(duplicated);

        foreach (var group in duplicated)
            Assert.Equal(group.Count(), group.Select(c => c.Uid).Distinct().Count());
    }

    /// <summary>
    /// Uids come from build order, not a running counter, so every client building the
    /// same deck agrees on them. A per-process counter would number the same card
    /// differently on each machine and desync any future masked multiplayer.
    /// </summary>
    [Fact]
    public void The_same_deck_built_twice_gets_the_same_uids()
    {
        var first  = DeckBuilder.Build("standard-104");
        var second = DeckBuilder.Build("standard-104");

        Assert.Equal(
            first.Select(c => (c.Id, c.Uid)),
            second.Select(c => (c.Id, c.Uid)));
    }

    [Fact]
    public void Shuffling_reorders_cards_without_renaming_them()
    {
        var deck   = DeckBuilder.Build("standard-52");
        var before = deck.Select(c => (c.Id, c.Uid)).OrderBy(x => x.Uid).ToList();

        DeckBuilder.Shuffle(deck, new SeededRandomSource(4));

        Assert.Equal(before, deck.Select(c => (c.Id, c.Uid)).OrderBy(x => x.Uid));
    }

    [Fact]
    public async Task Uids_stay_unique_once_a_game_is_dealt()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()));

        await vm.StartAsync("pinochle", 4, resume: false, seed: 5);

        var all = vm.State!.Zones.Values.SelectMany(z => z.Cards).ToList();
        Assert.Equal(all.Count, all.Select(c => c.Uid).Distinct().Count());
    }

    /// <summary>
    /// A resumed game must keep the same cards, not merely equivalent ones. Renumbering
    /// on load would read to the renderer as every card being new, animating a whole
    /// table on resume.
    /// </summary>
    [Fact]
    public async Task Uids_survive_a_save_and_resume()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        var store = new RecordingStore();
        var saves = new GameSaveService(store);

        var vm = new GameTableViewModel(loader, saves);
        await vm.StartAsync("pinochle", 4, resume: false, seed: 5);
        await vm.SaveAsync();

        var expected = vm.State!.Zones
            .ToDictionary(z => z.Key, z => z.Value.Cards.Select(c => c.Uid).ToList());

        var resumed = new GameTableViewModel(loader, saves);
        await resumed.StartAsync("pinochle", 4, resume: true, resumeSlotId: vm.SlotId);

        var actual = resumed.State!.Zones
            .ToDictionary(z => z.Key, z => z.Value.Cards.Select(c => c.Uid).ToList());

        Assert.Equal(expected, actual);
    }

    private sealed class RecordingStore : ISaveStore
    {
        private readonly Dictionary<string, string> _items = [];
        public bool Exists(string key) => _items.ContainsKey(key);
        public void Delete(string key) => _items.Remove(key);
        public Task WriteAsync(string key, string contents) { _items[key] = contents; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string key) => Task.FromResult(_items.GetValueOrDefault(key));
    }
}
