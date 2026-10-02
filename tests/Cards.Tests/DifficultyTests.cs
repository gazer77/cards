using Cards.Engine;
using Cards.Models;

namespace Cards.Tests;

/// <summary>
/// The computer at each level: easy plays worse than normal, hard plays better, and the
/// level the table chose is the level it keeps — through the deal, a save and a resume.
///
/// "Better" is measured, not asserted from the code: a seat at each level plays a run of
/// seeded games against normal opponents, and its results are compared.
/// </summary>
public sealed class DifficultyTests
{
    private static GameDefinition Def(string id) => TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), id)!;

    private static IPlayerAgent Agent(string level, string id, IRandomSource rng) => level switch
    {
        Difficulty.Easy => new EasyPlayer(new SmartDefaultAiAgent(id, rng), rng),
        Difficulty.Hard => new SmartDefaultAiAgent(id, rng) { Hard = true },
        _               => new SmartDefaultAiAgent(id, rng),
    };

    /// <summary>Plays a game to the end with the given level at the given seats and normal elsewhere.</summary>
    private static GameState Play(string gameId, int players, ulong seed, string level, params int[] seats)
    {
        var definition = Def(gameId);
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, []);
        for (int i = 0; i < state.Players.Count; i++)
            state.PlayerAgents[state.Players[i].Id] = Agent(seats.Contains(i) ? level : Difficulty.Normal, state.Players[i].Id, state.Rng);

        for (int step = 0; step < 20000 && !logic.IsGameOver(state); step++)
            logic.Apply(state, logic.GetAutoAction(state));
        Assert.True(logic.IsGameOver(state), $"{gameId} seed {seed} at {level} did not finish.");
        return state;
    }

    private const int Games = 40;

    /// <summary>Hearts: the average of a seat's final score — points taken, so lower is better.</summary>
    private static double HeartsScore(string level)
        => Enumerable.Range(1, Games).Average(s => Play("hearts", 4, (ulong)s, level, 0).GetScore("player0"));

    /// <summary>A partnership game: how many games seats 0 and 2 win together.</summary>
    private static int TeamWins(string gameId, string level)
        => Enumerable.Range(1, Games).Count(s =>
        {
            var state = Play(gameId, 4, (ulong)s, level, 0, 2);
            var mine  = state.GetPlayerTeam("player0")!;
            return state.Teams.All(t => t == mine || state.GetTeamScore(mine.Id) > state.GetTeamScore(t.Id));
        });

    private static int CribbageWins(string level)
        => Enumerable.Range(1, Games).Count(s =>
        {
            var state = Play("cribbage", 2, (ulong)s, level, 0);
            return state.GetScore("player0") > state.GetScore("player1");
        });

    // ── The ladder, measured ──────────────────────────────────────────────────
    //
    // Seeded, so each is the same every run: these hold the levels apart, and a change
    // that closes the gap — or turns it round — fails here. Where hard is no better than
    // normal (Spades, Whist), it is held to being no worse.

    [Fact]
    public void Hearts_hard_takes_far_fewer_points_and_easy_no_fewer()
    {
        double easy = HeartsScore(Difficulty.Easy), normal = HeartsScore(Difficulty.Normal), hard = HeartsScore(Difficulty.Hard);
        Assert.True(hard < normal - 20, $"hard {hard:F1} vs normal {normal:F1}");
        Assert.True(easy >= normal - 3, $"easy {easy:F1} vs normal {normal:F1}");
    }

    [Theory]
    [InlineData("spades", false)] [InlineData("whist", false)] [InlineData("euchre", true)]
    public void A_partnership_wins_less_at_easy_and_no_less_at_hard(string gameId, bool hardWinsMore)
    {
        int easy = TeamWins(gameId, Difficulty.Easy), normal = TeamWins(gameId, Difficulty.Normal), hard = TeamWins(gameId, Difficulty.Hard);
        Assert.True(easy < normal, $"{gameId}: easy {easy} vs normal {normal}");
        Assert.True(hardWinsMore ? hard > normal : hard >= normal - 3, $"{gameId}: hard {hard} vs normal {normal}");
    }

    [Fact]
    public void Cribbage_wins_less_at_easy_and_more_at_hard()
    {
        int easy = CribbageWins(Difficulty.Easy), normal = CribbageWins(Difficulty.Normal), hard = CribbageWins(Difficulty.Hard);
        Assert.True(easy < normal, $"easy {easy} vs normal {normal}");
        Assert.True(hard > normal, $"hard {hard} vs normal {normal}");
    }

    // ── The level the table chose ─────────────────────────────────────────────

    [Theory]
    [InlineData(Difficulty.Easy)] [InlineData(Difficulty.Normal)] [InlineData(Difficulty.Hard)]
    public void The_computer_is_dealt_in_at_the_tables_level(string level)
    {
        var definition = Def("hearts");
        var state = new GameState { GameId = definition.Id, Definition = definition, Difficulty = level };
        LogicRegistry.Create(definition).Initialize(state, 4, []);

        var bots = state.Players.Skip(1).Select(p => state.PlayerAgents[p.Id]).ToList();
        Assert.All(bots, a =>
        {
            if (level == Difficulty.Easy) Assert.IsType<EasyPlayer>(a);
            else Assert.Equal(level == Difficulty.Hard, Assert.IsType<SmartDefaultAiAgent>(a).Hard);
        });
    }

    [Fact]
    public void Go_fish_keeps_its_own_player_at_the_tables_level()
    {
        var definition = Def("go-fish");
        var state = new GameState { GameId = definition.Id, Definition = definition, Difficulty = Difficulty.Easy };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 3, []);
        logic.GetValidActions(state);

        Assert.All(state.Players.Skip(1), p => Assert.IsType<EasyPlayer>(state.PlayerAgents[p.Id]));
    }

    [Fact]
    public void A_saved_game_resumes_at_the_level_it_was_played()
    {
        var definition = Def("spades");
        var played = new GameState { GameId = definition.Id, Definition = definition, Difficulty = Difficulty.Hard };
        LogicRegistry.Create(definition).Initialize(played, 4, []);

        var saved   = GameStateSerializer.Snapshot(played, 4, []);
        var resumed = new GameState { GameId = definition.Id, Definition = definition };
        GameStateSerializer.Restore(resumed, LogicRegistry.Create(definition), saved, 4, []);

        Assert.Equal(Difficulty.Hard, resumed.Difficulty);
        Assert.True(Assert.IsType<SmartDefaultAiAgent>(resumed.PlayerAgents["player1"]).Hard);
    }

    [Fact]
    public void Easy_is_only_careless_where_there_is_a_choice_to_make()
    {
        // Picking cards to meld and confirming are not decisions: a careless player still
        // has to finish its turn, so those go to the player it wraps.
        var inner = new FirstChoice("player1");
        var easy  = new EasyPlayer(inner, new SeededRandomSource(1));
        var bookkeeping = new List<GameAction> { new("lay_meld"), new("meld_done"), new("discard") };

        for (int i = 0; i < 50; i++) Assert.Equal("lay_meld", easy.ChooseAction(Blank, bookkeeping).Type);

        // Cards to play are: some of the time it picks one that is not the inner player's.
        var cards = Enumerable.Range(0, 8).Select(i => new GameAction("play_card", CardId: $"c{i}")).ToList();
        int careless = Enumerable.Range(0, 200).Count(_ => easy.ChooseAction(Blank, cards).CardId != "c0");
        Assert.InRange(careless, 50, 120);
    }

    private static readonly GameState Blank = new() { GameId = "x", Definition = new GameDefinition() };

    private sealed class FirstChoice(string id) : IPlayerAgent
    {
        public string PlayerId => id;
        public GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions) => validActions[0];
    }

    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("DIFFICULTY_REPORT") is not ("1" or "true")) return;
        foreach (var level in Difficulty.All)
            Console.WriteLine($"[difficulty] {level,-6} hearts avg {HeartsScore(level):F1}  spades {TeamWins("spades", level)}/{Games}  " +
                              $"whist {TeamWins("whist", level)}/{Games}  euchre {TeamWins("euchre", level)}/{Games}  cribbage {CribbageWins(level)}/{Games}");
    }
}
