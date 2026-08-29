using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Plays every game at every seat count it advertises, and reports which ones actually
/// finish.
///
/// Driven the way a player drives a table, not the way the golden-master runner does.
/// That runner only applies actions the engine offers, and several games offer none
/// until a card has been tapped — tapping is how a rank or a play is chosen. Those
/// games therefore sit on the opening deal until the step cap, which is why half the
/// recorded golden masters cover almost none of their game and why a Go Fish bug lived
/// there undetected.
///
/// Reported rather than asserted: this is a survey of where single-player stands, and a
/// game that cannot finish is a bug to be fixed, not a threshold to be tuned.
/// Run with: SWEEP=1 dotnet test --filter PlayabilitySweep
/// </summary>
public sealed class PlayabilitySweep
{
    /// <summary>
    /// Step budget. Raise it with SWEEP_STEPS to tell a long game from a stuck one --
    /// War legitimately takes hundreds of steps, and a game played to a target score
    /// takes many hands.
    /// </summary>
    private static int MaxSteps =>
        int.TryParse(Environment.GetEnvironmentVariable("SWEEP_STEPS"), out var n) ? n : 4000;

    private sealed record Outcome(
        string GameId, int Seats, bool Finished, int Steps, string Phase, string Reason);

    [Fact]
    public async Task Report()
    {
        if (Environment.GetEnvironmentVariable("SWEEP") is not ("1" or "true")) return;

        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        var games = await loader.LoadAllAsync();
        var results = new List<Outcome>();

        foreach (var game in games.OrderBy(g => g.Id, StringComparer.Ordinal))
            for (int seats = game.MinPlayers; seats <= game.MaxPlayers; seats++)
                results.Add(Run(loader, game.Id, seats));

        Console.WriteLine();
        Console.WriteLine("[sweep] game / seats / result / steps / final phase");

        foreach (var r in results)
            Console.WriteLine(
                $"[sweep] {r.GameId,-16} {r.Seats}p  {(r.Finished ? "ok      " : "STALLED "),-8} " +
                $"{r.Steps,5}  {r.Phase}{(r.Reason.Length > 0 ? "  " + r.Reason : "")}");

        var stalled = results.Where(r => !r.Finished).ToList();
        Console.WriteLine();
        Console.WriteLine($"[sweep] {results.Count - stalled.Count} of {results.Count} configurations finish.");

        foreach (var group in stalled.GroupBy(s => s.GameId).OrderBy(g => g.Key, StringComparer.Ordinal))
            Console.WriteLine($"[sweep] STALLED {group.Key}: {string.Join(", ", group.Select(s => s.Seats + "p"))}");
    }

    /// <summary>
    /// The single card currently selected, or null. Multi-select phases hold a
    /// comma-separated list and are submitted with an action rather than a drop.
    /// </summary>
    private static string? Selected(GameState state)
    {
        var value = state.Metadata.GetValueOrDefault("selected_card", "");
        return string.IsNullOrEmpty(value) || value.Contains(',') ? null : value;
    }

    /// <summary>
    /// Enough of the position to tell whether the game has moved on: phase, whose turn,
    /// round, scores and how many cards sit in each zone.
    /// </summary>
    private static string Signature(GameState state) =>
        string.Join('|',
            state.CurrentPhaseId,
            state.CurrentPlayerIndex,
            state.RoundNumber,
            string.Join(',', state.Scores.OrderBy(s => s.Key, StringComparer.Ordinal)
                                         .Select(s => $"{s.Key}={s.Value}")),
            string.Join(',', state.Zones.OrderBy(z => z.Key, StringComparer.Ordinal)
                                        .Select(z => $"{z.Key}:{z.Value.Count}")));

    private static int CountCards(GameState state)
        => state.Zones.Values.Sum(z => z.Count);

    private static Outcome Run(GameLoader loader, string gameId, int seats)
    {
        var definition = loader.LoadAsync(gameId).GetAwaiter().GetResult();
        if (definition is null) return new(gameId, seats, false, 0, "-", "definition failed to load");

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(17),
        };

        IGameLogic logic;
        try
        {
            logic = LogicRegistry.Create(definition);
            logic.Initialize(state, seats, []);
        }
        catch (Exception ex)
        {
            return new(gameId, seats, false, 0, "-", $"threw on setup: {ex.GetType().Name}");
        }

        int steps = 0;
        int tap = 0;
        string reason = "";
        int cardCount = CountCards(state);

        // A rolling window of recent positions. A game making progress keeps producing
        // new ones; a livelocked game revisits a handful forever.
        var recent = new Queue<string>();

        try
        {
            while (steps < MaxSteps && !logic.IsGameOver(state))
            {
                var result = TableDriver.Step(state, logic, ref tap);

                if (result == TableDriver.StepResult.Stuck)
                {
                    reason = "nothing on offer";
                    break;
                }
                if (result == TableDriver.StepResult.Finished) break;

                steps++;

                // Cards must not leave the game. Golf drew into a hand zone it never
                // declared, so every draw removed a card from the deck and dropped it
                // nowhere — the null-conditional that added it silently did nothing.
                // The symptom was a livelock; the cause was a leak.
                int now = CountCards(state);
                if (now != cardCount)
                {
                    reason = $"CARDS LOST — {cardCount} became {now} at step {steps}";
                    break;
                }

                recent.Enqueue(Signature(state));
                if (recent.Count > 400) recent.Dequeue();
            }

            if (steps >= MaxSteps)
            {
                // "Ran long" and "went round in circles" are different bugs with
                // different fixes, and the step cap alone cannot tell them apart —
                // War legitimately takes hundreds of steps and finishes.
                int distinct = recent.Distinct().Count();
                reason = distinct <= 12
                    ? $"LIVELOCK — last {recent.Count} steps cycled through {distinct} positions"
                    : $"ran long — {distinct} distinct positions in last {recent.Count} steps";
            }
        }
        catch (Exception ex)
        {
            reason = $"threw: {ex.GetType().Name}: {ex.Message}";
        }

        return new(gameId, seats, logic.IsGameOver(state), steps, state.CurrentPhaseId, reason);
    }
}
