using System.Security.Cryptography;
using System.Text;
using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Drives a game to completion with every seat played by the AI, folding the state
/// after each step into a running hash.
///
/// This is the characterization harness: it does not assert that the rules are
/// *correct*, only that they do not silently *change*.  That is what makes it safe
/// to move ~10k lines of engine into a new assembly.
/// </summary>
public static class EngineRunner
{
    /// <summary>
    /// Upper bound on steps, so a rules bug becomes a failing test rather than a hung run.
    /// Kept high because several games legitimately run past 3000 steps before ending.
    /// </summary>
    public const int MaxSteps = 5000;

    /// <summary>
    /// Games that are both expensive per step and never reach game over under all-AI
    /// play, so a lower budget truncates a run that was going to be truncated anyway.
    ///
    /// poker-wilds evaluates every wild substitution when ranking a hand, which is
    /// combinatorial; at 9 seats and 5000 steps a single case took over four minutes.
    /// </summary>
    private static readonly Dictionary<string, int> StepBudget = new()
    {
        ["poker-wilds"] = 500,
    };

    public sealed record Result(string Digest, int Steps, bool ReachedGameOver, string FinalPhase);

    public static async Task<Result> RunAsync(
        GameLoader loader, string gameId, int playerCount, ulong seed)
    {
        var definition = await loader.LoadAsync(gameId)
            ?? throw new InvalidOperationException($"Game definition '{gameId}' failed to load.");

        var state = new GameState { GameId = definition.Id, Definition = definition };

        // Seed BEFORE Initialize: setup deals cards and picks the first dealer.
        var rng    = new SeededRandomSource(seed);
        state.Rng  = rng;
        state.Seed = seed;

        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, playerCount, []);

        // The app leaves seat 0 to the human, so nothing would drive it here.
        // Give every seat an agent so the run is fully automated.
        foreach (var p in state.Players)
            if (!state.PlayerAgents.ContainsKey(p.Id))
                state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Fold(sha, state);

        int budget = StepBudget.GetValueOrDefault(gameId, MaxSteps);
        int steps  = 0;
        bool over  = logic.IsGameOver(state);

        while (!over && steps < budget)
        {
            // GetAutoAdvanceDelay is the engine's way of saying "no human input needed
            // here". The duration itself is a pacing hint for the UI; we ignore it and
            // tick immediately.
            if (logic.GetAutoAdvanceDelay(state) is null) break;

            var valid = logic.GetValidActions(state);
            var sel   = logic.GetSelectableCardIds(state);

            // A card-play phase advertises no valid *actions* — its affordance is the
            // set of selectable cards, which GetAutoAction turns into play_card.
            // Checking only GetValidActions (as GameTablePage's loop does) would stall
            // every trick-taking and draw-discard game on its first turn.
            if (valid.Count == 0 && sel.Count == 0) break;

            // GameTablePage stops here and shows a Ready button so players can see the
            // revealed hands. Headless, we just apply it and keep going, otherwise every
            // poker game would stall at the first showdown.
            var action = valid.Count == 1 && sel.Count == 0 && valid[0].Type == "ready"
                ? valid[0]
                : logic.GetAutoAction(state);

            logic.Apply(state, action);
            steps++;

            Fold(sha, state);

            over = logic.IsGameOver(state);
        }

        string digest = Convert.ToHexString(sha.GetHashAndReset())[..32];
        return new Result(digest, steps, over, state.CurrentPhaseId);
    }

    /// <summary>
    /// Folds the observable state into the hash.
    ///
    /// Deliberately keyed on <see cref="Card.Id"/> and position, NOT on any card
    /// identity field — so adding Card.Uid later must leave every hash untouched.
    /// </summary>
    private static void Fold(IncrementalHash sha, GameState state)
    {
        var sb = new StringBuilder(1024);

        sb.Append(state.CurrentPhaseId).Append('|')
          .Append(state.CurrentPlayerIndex).Append('|')
          .Append(state.RoundNumber).Append('|')
          .Append(state.DealerId ?? "-").Append('\n');

        foreach (var zoneId in state.Zones.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var zone = state.Zones[zoneId];
            sb.Append(zoneId).Append(':');
            foreach (var c in zone.Cards)
                sb.Append(c.Id).Append(c.IsFaceUp ? '^' : 'v').Append(c.IsWild ? '*' : '.').Append(',');
            sb.Append('\n');
        }

        foreach (var k in state.Scores.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(state.Scores[k]).Append(';');
        sb.Append('\n');

        foreach (var k in state.Metadata.Keys.OrderBy(k => k, StringComparer.Ordinal))
            sb.Append(k).Append('=').Append(state.Metadata[k]).Append(';');
        sb.Append('\n');

        sha.AppendData(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
