using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Speech bubbles go beside the seat a line is about, carry what was said and not the
/// instruction after it, and never show instructions at all.
///
/// They used to be pinned to whoever had just acted. A handler writes its line and then
/// passes the turn, so "Dealer: 13 — Tap for next card" appeared beside a player, the
/// next player's hand total beside the one who had just stood, and "Tap to collect"
/// arrived as a box glyph in the middle of a sentence.
/// </summary>
public sealed class BubbleTests
{
    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static (GameTableViewModel Vm, List<(string Seat, string Text)> Said) Table()
    {
        var loader = new GameLoader(new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore())) { TurnPace = 0.001 };
        var said = new List<(string, string)>();
        vm.MessagePosted += (seat, text) => said.Add((seat, text));
        return (vm, said);
    }

    [Fact]
    public async Task The_dealer_speaks_from_the_dealers_seat_and_each_result_from_its_own()
    {
        var (vm, said) = Table();
        await vm.StartAsync("blackjack", 2, resume: false, seed: 11);

        for (int i = 0; i < 40 && !said.Any(s => s.Text.Contains("wins") || s.Text.Contains("bust") || s.Text.Contains("push")); i++)
        {
            var stand = vm.Actions.FirstOrDefault(a => a.Type == "stand");
            if (stand is not null) await vm.Invoke(stand);
            else await vm.TapTable();
        }

        string dealer = vm.State!.Players.Single(p => p.Role is not null).Id;
        Assert.All(said.Where(s => s.Text.StartsWith("Dealer:")), s => Assert.Equal(dealer, s.Seat));
        Assert.Contains(said, s => s.Seat == "player0" && (s.Text.StartsWith("You") || s.Text.StartsWith("Blackjack")));
    }

    [Fact]
    public async Task A_bubble_is_what_was_said_not_the_instruction_after_it()
    {
        var (vm, said) = Table();
        await vm.StartAsync("war", 2, resume: false, seed: 5);

        for (int i = 0; i < 12 && !vm.IsGameOver; i++) await vm.TapTable();

        Assert.NotEmpty(said);
        Assert.All(said, s => Assert.DoesNotContain('\n', s.Text));
        Assert.DoesNotContain(said, s => s.Text.StartsWith("Tap"));

        // A round's result is beside its winner.
        foreach (var (seat, text) in said.Where(s => s.Text.StartsWith("You win this round")))
            Assert.Equal("player0", seat);
        foreach (var (seat, text) in said.Where(s => s.Text.StartsWith("Opponent wins")))
            Assert.Equal("player1", seat);
    }

    [Fact]
    public void Instructions_belong_to_nobody()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("hand-and-foot").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition };
        LogicRegistry.Create(definition).Initialize(state, 2, []);

        string prompt = GameText.Message(state, "turn_draw", "{player}'s turn — Draw a card", "player1");
        string done   = GameText.Message(state, "log_discarded_card", "{player} discarded the {card}", "player1", ("card", "7 of Hearts"));

        Assert.Equal(GameText.Nobody, GameText.SubjectOf(state, prompt, "player0"));
        Assert.Equal("player1", GameText.SubjectOf(state, done, "player0"));
    }
}
