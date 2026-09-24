using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Poker as one entry in the picker with its variants named inside it.
///
/// Hold'em and Stud share a deck, a betting vocabulary and the word poker — no fact
/// about a table says which one you meant, which is why these are named shapes rather
/// than shapes matched against the seat count.
/// </summary>
public sealed class PokerConfigTests
{
    private static Cards.Models.GameDefinition Poker()
        => new GameLoader(new EmbeddedGameAssetSource()).LoadAsync("poker").GetAwaiter().GetResult()!;

    private static (GameState State, IGameLogic Logic) Seated(int seats, string? variant)
    {
        var definition = Poker();
        var state = new GameState
        {
            GameId = definition.Id,
            Definition = definition,
            ConfigurationName = variant,
            Rng = new SeededRandomSource(5),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);
        return (state, logic);
    }

    private static GameState Table(int seats, string? variant) => Seated(seats, variant).State;

    [Fact]
    public void One_entry_covers_three_games()
    {
        var games = new GameLoader(new EmbeddedGameAssetSource()).LoadAllAsync().GetAwaiter().GetResult();

        Assert.DoesNotContain(games, g => g.Id is "texas-holdem" or "poker-stud" or "poker-wilds");
        var poker = Assert.Single(games.Where(g => g.Id == "poker"));

        Assert.Equal(["Texas Hold'em", "Deuces Wild", "Seven-Card Stud"],
                     GameConfiguration.Offered(poker, 6).Select(c => c.Name));
    }

    [Fact]
    public void Nobody_choosing_gets_holdem()
    {
        var state = Table(4, variant: null);

        Assert.Contains(state.Zones.Keys, id => id == "community");
        Assert.NotNull(state.Definition.Blinds);
        Assert.Equal("Texas Hold'em", GameConfiguration.DefaultFor(Poker(), 4)!.Name);
    }

    [Fact]
    public void Stud_is_a_different_game_underneath_the_same_name()
    {
        var stud = Table(4, "Seven-Card Stud");

        // No community cards, an ante instead of blinds, and its own street-by-street
        // phases — a configuration that mentions an array means that array.
        Assert.DoesNotContain(stud.Zones.Keys, id => id.StartsWith("community"));
        Assert.Null(stud.Definition.Blinds);
        Assert.NotNull(stud.Definition.Ante);
        Assert.Contains(stud.Definition.Phases, p => p.Id == "deal_third_street");
        Assert.DoesNotContain(stud.Definition.Phases, p => p.Id == "flop");
    }

    [Fact]
    public void The_wild_variant_is_holdem_with_the_twos_wild()
    {
        var wild = Table(4, "Deuces Wild");

        Assert.Contains(wild.Zones.Keys, id => id == "community");   // still Hold'em
        Assert.Contains(wild.Definition.Phases, p => p.Id == "flop");

        var wilds = wild.Definition.Scoring!.Extra!["wilds"];
        Assert.Equal(1, wilds.GetArrayLength());
        Assert.Equal("2", wilds[0].GetProperty("rank").GetString());
    }

    [Fact]
    public void A_variant_only_offers_the_house_rules_that_belong_to_it()
    {
        // Razz patches a stud phase and One-Eyed Jacks patches a wild-card list; each
        // against the wrong game would be a rule with nothing to change.
        Assert.Contains(Table(4, "Seven-Card Stud").Definition.HouseRules, r => r.Id == "razz");
        Assert.DoesNotContain(Table(4, "Texas Hold'em").Definition.HouseRules, r => r.Id == "razz");

        Assert.Contains(Table(4, "Deuces Wild").Definition.HouseRules, r => r.Id == "one_eyed_jacks");
        Assert.DoesNotContain(Table(4, "Texas Hold'em").Definition.HouseRules, r => r.Id == "one_eyed_jacks");
    }

    [Fact]
    public void A_ninth_seat_takes_stud_off_the_table()
    {
        // Stud seats eight and Hold'em nine. A shape may be both named and matched, and
        // this one is offered only where it can be dealt.
        var poker = Poker();

        Assert.Contains(GameConfiguration.Offered(poker, 8), c => c.Name == "Seven-Card Stud");
        Assert.DoesNotContain(GameConfiguration.Offered(poker, 9), c => c.Name == "Seven-Card Stud");
    }

    [Fact]
    public void Every_variant_deals_a_table_that_plays()
    {
        foreach (var variant in new[] { "Texas Hold'em", "Deuces Wild", "Seven-Card Stud" })
        {
            var (state, logic) = Seated(4, variant);

            Assert.Equal(4, state.Players.Count);
            Assert.All(state.Players, p => Assert.True(state.Scores.GetValueOrDefault(p.Id) > 0
                                                    || state.Metadata.ContainsKey("pot")));

            // And it gets past the opening deal into a betting round under its own steam.
            for (int i = 0; i < 40 && state.CurrentPhaseId.StartsWith("deal"); i++)
                logic.Apply(state, logic.GetAutoAction(state));

            Assert.StartsWith("bet", state.CurrentPhaseId);
        }
    }
}

/// <summary>
/// A save remembers which shape it was written under. Without that, resuming a Stud
/// game deals Hold'em onto a Stud position: no community zone for the cards to land in
/// and a betting phase that does not exist.
/// </summary>
public sealed class ConfigurationSaveTests
{
    private sealed class MemoryStore : Cards.Engine.ISaveStore
    {
        private readonly Dictionary<string, string> _files = [];
        public bool Exists(string key) => _files.ContainsKey(key);
        public void Delete(string key) => _files.Remove(key);
        public Task WriteAsync(string key, string contents) { _files[key] = contents; return Task.CompletedTask; }
        public Task<string?> ReadAsync(string key) => Task.FromResult(_files.GetValueOrDefault(key));
    }

    [Fact]
    public async Task A_stud_game_resumes_as_stud()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var saves  = new Cards.Services.GameSaveService(new MemoryStore());
        var vm     = new Cards.App.GameTableViewModel(loader, saves) { TurnPace = 0 };

        Assert.True(await vm.StartAsync("poker", 4, resume: false, seed: 9, configuration: "Seven-Card Stud"));
        Assert.Contains(vm.State!.Definition.Phases, p => p.Id == "deal_third_street");

        await saves.SaveAsync(vm.State, 4, []);
        var slot = saves.SavesFor("poker").Single();

        var resumed = new Cards.App.GameTableViewModel(loader, saves) { TurnPace = 0 };
        Assert.True(await resumed.StartAsync("poker", 4, resume: true, resumeSlotId: slot.Id));

        Assert.Equal("Seven-Card Stud", resumed.State!.ConfigurationName);
        Assert.Contains(resumed.State.Definition.Phases, p => p.Id == "deal_third_street");
        Assert.DoesNotContain(resumed.State.Zones.Keys, id => id == "community");
    }
}
