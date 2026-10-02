using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// What a table sounds like: each move heard from the table before and after it — a
/// card played, drawn, turned, a trick gathered, a deal — then points, then a bell when
/// it comes round to you, and at the end a win or a loss.
/// </summary>
public sealed class TableSoundsTests
{
    private sealed class Ear : ITableSounds
    {
        public readonly List<TableCue> Heard = [];
        public void Play(TableCue cue) { lock (Heard) Heard.Add(cue); }
    }

    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static (GameState State, IGameLogic Logic) Start(string id, int players, ulong seed = 1)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), id)!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, []);
        return (state, logic);
    }

    [Fact]
    public void A_card_played_to_a_trick_is_a_play_and_then_the_trick_is_gathered()
    {
        var (state, logic) = Start("whist", 4);
        foreach (var p in state.Players) state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        var heard = new List<TableCue>();
        for (int i = 0; i < 12; i++)
        {
            var before = TableSounds.Capture(state, logic.IsGameOver(state));
            logic.Apply(state, logic.GetAutoAction(state));
            heard.AddRange(TableSounds.Between(before, state, logic.IsGameOver(state), state.Viewer));
        }

        Assert.Contains(TableCue.Play, heard);
        Assert.Contains(TableCue.Gather, heard);
        Assert.DoesNotContain(TableCue.Draw, heard);   // nothing is drawn in whist
    }

    [Fact]
    public void A_card_drawn_from_the_deck_is_a_draw()
    {
        var (state, logic) = Start("gin-rummy", 2);
        state.CurrentPlayerIndex = 0;
        var before = TableSounds.Capture(state, false);

        var draw = logic.GetValidActions(state).First(a => a.Type.StartsWith("draw_from_deck"));
        logic.Apply(state, draw);

        Assert.Equal(TableCue.Draw, TableSounds.Between(before, state, false, "player0")[0]);
    }

    [Fact]
    public void It_rings_when_it_comes_round_to_you_and_not_after_your_own_move()
    {
        var (state, _) = Start("hearts", 4);
        state.CurrentPlayerIndex = 3;
        var theirs = TableSounds.Capture(state, false);
        state.CurrentPlayerIndex = 0;
        Assert.Contains(TableCue.YourTurn, TableSounds.Between(theirs, state, false, "player0"));

        var mine = TableSounds.Capture(state, false);
        Assert.DoesNotContain(TableCue.YourTurn, TableSounds.Between(mine, state, false, "player0"));
    }

    [Theory]
    [InlineData("player0", TableCue.Win)]
    [InlineData("player1", TableCue.Lose)]
    public void The_end_is_a_win_or_a_loss_as_you_see_it(string winner, TableCue expected)
    {
        var (state, _) = Start("hearts", 4);
        var before = TableSounds.Capture(state, false);
        state.Metadata["last_winner"] = winner;

        Assert.Equal([expected], TableSounds.Between(before, state, true, "player0"));
    }

    [Fact]
    public void A_partners_win_is_your_win()
    {
        var (state, _) = Start("spades", 4);
        var before = TableSounds.Capture(state, false);
        state.Metadata["last_winner"] = state.GetPlayerTeam("player0")!.Id;

        Assert.Equal([TableCue.Win], TableSounds.Between(before, state, true, "player0"));
    }

    /// <summary>A whole table through the view model, as the browser runs it: it is heard.</summary>
    [Fact]
    public async Task A_table_makes_its_sounds_as_it_plays()
    {
        var ear = new Ear();
        var vm  = new GameTableViewModel(new GameLoader(new EmbeddedGameAssetSource()), new GameSaveService(new NoSaveStore()))
        {
            TurnPace = 0.001, Sounds = ear,
        };
        await vm.StartAsync("whist", 4, resume: false, seed: 4);

        // We play, the computer plays round to us again: cards, the trick gathered, the bell.
        for (int i = 0; i < 30 && !(ear.Heard.Contains(TableCue.YourTurn) && ear.Heard.Contains(TableCue.Gather)); i++)
        {
            if (vm.SelectableCardIds.Count > 0) await vm.Invoke(new GameAction("play_card", CardId: vm.SelectableCardIds[0]));
            else if (vm.Actions.FirstOrDefault() is { } a) await vm.Invoke(a);
        }

        Assert.Contains(TableCue.Play, ear.Heard);
        Assert.Contains(TableCue.Gather, ear.Heard);
        Assert.Contains(TableCue.YourTurn, ear.Heard);
    }

    [Fact]
    public void Every_sound_is_a_playable_wav()
    {
        foreach (var make in new Func<byte[]>[] { SoundGenerator.Shuffle, SoundGenerator.Deal, SoundGenerator.Play, SoundGenerator.Draw,
                                                  SoundGenerator.Flip, SoundGenerator.Gather, SoundGenerator.Score, SoundGenerator.YourTurn,
                                                  SoundGenerator.Win, SoundGenerator.Lose })
        {
            var wav = make();
            Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
            Assert.True(wav.Length > 44 + 200, $"{make.Method.Name} is all but silent.");
            Assert.True(wav.Length < 22050 * 2 * 2, $"{make.Method.Name} runs over a second.");
        }
    }
}
