using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// How long a computer player's turn takes to watch, at the default pace.
///
/// A table waits before each step the computer takes: the engine's delay, stretched to
/// the player's pace and never under the minimum pause — except a step that finishes
/// the move just made (<see cref="IGameLogic.ContinuesMove"/>), which follows at the
/// engine's own beat. A turn of many steps paced like many turns is slow, and reads as
/// the computer stopping and starting: Golf's flip after a discard, a meld picked up a
/// card at a time.
/// </summary>
public sealed class PacingTests
{
    // The browser's defaults (Settings: pace 1.8; Play.razor: a 650 ms floor).
    private const double DefaultPace = 1.8;
    private static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(650);

    private static TimeSpan Wait(IGameLogic logic, GameState state, TimeSpan delay)
    {
        if (logic.ContinuesMove(state)) return delay;
        var scaled = delay * DefaultPace;
        return scaled < Floor ? Floor : scaled;
    }

    /// <summary>Seconds a table waits through each computer turn, from the first step to the turn passing.</summary>
    public static List<double> TurnLengths(string gameId, int players, ulong seed, int steps = 1500)
    {
        var definition = TestGames.Load(new GameLoader(new EmbeddedGameAssetSource()), gameId)!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(seed) };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, players, []);
        foreach (var p in state.Players)
            if (!state.PlayerAgents.ContainsKey(p.Id)) state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        var turns = new List<double>();
        string? seat = null;
        double running = 0;
        for (int i = 0; i < steps && !logic.IsGameOver(state); i++)
        {
            if (logic.GetAutoAdvanceDelay(state) is not { } delay) break;
            string now = state.CurrentPlayer.Id;
            if (now != seat && seat is not null) { turns.Add(running); running = 0; }
            seat = now;
            running += Wait(logic, state, delay).TotalSeconds;
            logic.Apply(state, logic.GetAutoAction(state));
        }
        return turns;
    }

    public static readonly (string Game, int Players)[] Tables =
    [
        ("hand-and-foot", 2), ("gin-rummy", 2), ("golf", 4), ("hearts", 4), ("cribbage", 2),
        ("go-fish", 3), ("euchre", 4), ("pinochle", 4), ("spades", 4), ("whist", 4),
    ];

    /// <summary>
    /// No computer turn drags. Before the pass that marked the rest of a turn as one move,
    /// Hand and Foot averaged 8.5 s a turn with a 37 s worst, and Pinochle's meld 22 s.
    /// The ceilings leave the table's reading pauses — a trick gathered, a hand counted —
    /// and catch a turn of many steps paced like many turns.
    /// </summary>
    [Theory]
    [InlineData("hand-and-foot", 2, 5.0, 15.0)]
    [InlineData("pinochle",      4, 3.5, 15.0)]
    [InlineData("golf",          4, 3.0, 12.0)]
    [InlineData("gin-rummy",     2, 3.5, 12.0)]
    [InlineData("hearts",        4, 3.0, 15.0)]
    [InlineData("cribbage",      2, 4.0, 15.0)]
    public void A_computer_turn_never_drags(string game, int players, double average, double longest)
    {
        var turns = TurnLengths(game, players, 3);
        Assert.True(turns.Average() <= average, $"{game}: an average turn takes {turns.Average():F1}s.");
        Assert.True(turns.Max() <= longest, $"{game}: the longest turn takes {turns.Max():F1}s.");
    }

    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("PACING_REPORT") is not ("1" or "true")) return;
        foreach (var (game, players) in Tables)
        {
            var t = TurnLengths(game, players, 3);
            Console.WriteLine($"[pacing] {game,-14} turns {t.Count,4}  avg {t.Average():F1}s  90th {t.OrderBy(x => x).ElementAt(t.Count * 9 / 10):F1}s  max {t.Max():F1}s");
        }
    }
}
