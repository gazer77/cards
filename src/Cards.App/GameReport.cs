using System.Text;
using Cards.Engine;

namespace Cards.App;

/// <summary>
/// Builds a plain-text report of a game in progress.
///
/// Written to be pasted straight into a bug report. A log of what happened is only half
/// of a card-game bug — the other half is where the cards actually are, which is
/// precisely what the game hides and what nobody can count reliably from a screenshot.
/// Both halves in one paste is the difference between a reproducible report and a
/// conversation.
/// </summary>
public static class GameReport
{
    public static string Build(
        GameTableViewModel vm,
        int seats,
        IReadOnlyList<string> houseRules,
        string? extraDiagnostics = null)
    {
        var state = vm.State;
        var sb = new StringBuilder();

        sb.AppendLine("=== Cards bug report ===");
        sb.AppendLine($"when      {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");

        if (state is null)
        {
            sb.AppendLine("state     (no game loaded)");
            return sb.ToString();
        }

        sb.AppendLine($"game      {state.Definition.Name} ({state.GameId} v{state.Definition.Version})");
        sb.AppendLine($"seats     {seats}");
        sb.AppendLine($"rules     {(houseRules.Count == 0 ? "(none)" : string.Join(", ", houseRules))}");
        sb.AppendLine($"sort      {vm.ActiveSortMode ?? "(game default)"}");
        sb.AppendLine($"phase     {state.CurrentPhaseId}  round {state.RoundNumber}");
        sb.AppendLine($"turn      {state.CurrentPlayer.Id} ({state.CurrentPlayer.Name})");
        sb.AppendLine($"over      {vm.IsGameOver}");
        sb.AppendLine($"status    {vm.StatusText}");

        if (!string.IsNullOrWhiteSpace(extraDiagnostics))
            sb.AppendLine($"render    {extraDiagnostics}");

        // The whole point: every card, and where it is.
        var all = state.Zones.Values.SelectMany(z => z.Cards).ToList();
        sb.AppendLine();
        sb.AppendLine($"--- cards ({all.Count} total) ---");

        foreach (var (id, zone) in state.Zones.OrderBy(z => z.Key, StringComparer.Ordinal))
            sb.AppendLine($"{id,-20} {zone.Count,3}  {string.Join(" ", zone.Cards.Select(c => c.Id))}");

        // Ranks short of a full set, which is what "cards have gone missing" looks like.
        var uneven = all
            .Where(c => c.Rank != Rank.Joker)
            .GroupBy(c => c.Rank)
            .Where(g => g.Count() % 4 != 0)
            .Select(g => $"{g.Key}x{g.Count()}")
            .ToList();

        sb.AppendLine(uneven.Count == 0
            ? "ranks                all complete"
            : $"ranks                UNEVEN: {string.Join(", ", uneven)}");

        if (state.Scores.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- scores ---");
            foreach (var (playerId, score) in state.Scores)
                sb.AppendLine($"{playerId,-20} {score}");
        }

        // Engine-private bookkeeping. Often the thing that explains an odd decision —
        // whose turn a handler thinks it is, what an AI believes about a hand.
        if (state.Metadata.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("--- metadata ---");
            foreach (var (key, value) in state.Metadata.OrderBy(m => m.Key, StringComparer.Ordinal))
                sb.AppendLine($"{key,-20} {value}");
        }

        sb.AppendLine();
        sb.AppendLine($"--- log ({vm.GameLog.Count} entries) ---");
        for (int i = 0; i < vm.GameLog.Count; i++)
            sb.AppendLine($"{i + 1,4}. {vm.GameLog[i]}");

        return sb.ToString();
    }

    /// <summary>A filename that sorts by time and says which game it came from.</summary>
    public static string FileName(GameTableViewModel vm)
        => $"cards-{vm.State?.GameId ?? "game"}-{DateTime.Now:yyyyMMdd-HHmmss}.txt";
}
