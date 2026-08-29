using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Every rank has exactly four cards, always, everywhere.
///
/// If a player holds three aces and the deck is empty, the fourth ace is in someone's
/// hand or someone's books — so asking for it must eventually work. Reported per seed
/// so a rare path shows up rather than hiding behind one lucky game.
/// </summary>
public sealed class GoFishRankCensus
{
    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("GOFISH") is not ("1" or "true")) return;

        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;

        int stalls = 0, censusFailures = 0, finished = 0;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            var state = new GameState
            {
                GameId = definition.Id, Definition = definition,
                Rng = new SeededRandomSource(seed),
            };
            var logic = LogicRegistry.Create(definition);
            logic.Initialize(state, 2, []);

            int step = 0;
            for (; step < 4000 && !logic.IsGameOver(state); step++)
            {
                var actions = logic.GetValidActions(state);
                if (actions.Count > 0) logic.Apply(state, logic.GetAutoAction(state));
                else
                {
                    var sel = logic.GetSelectableCardIds(state);
                    if (sel.Count == 0) break;
                    logic.Apply(state, new GameAction("select_card", CardId: sel[^1]));
                }

                var census = state.Zones.Values
                    .SelectMany(z => z.Cards)
                    .GroupBy(c => c.Rank)
                    .Where(g => g.Count() != 4)
                    .ToList();

                if (census.Count > 0)
                {
                    censusFailures++;
                    Console.WriteLine($"[census] seed {seed} step {step}: " +
                        string.Join(", ", census.Select(g => $"{g.Key}x{g.Count()}")));
                    break;
                }
            }

            if (logic.IsGameOver(state)) finished++;
            else
            {
                stalls++;
                if (stalls <= 3)
                    Console.WriteLine($"[stall] seed {seed} after {step} steps: deck " +
                        $"{state.Zones["deck"].Count}, " +
                        string.Join(", ", state.Zones
                            .Where(z => z.Key.StartsWith("hand:") || z.Key.StartsWith("books:"))
                            .Select(z => $"{z.Key}={z.Value.Count}")));
            }
        }

        Console.WriteLine($"[gf] 200 seeds: {finished} finished, {stalls} stalled, " +
                          $"{censusFailures} rank-census failures");
    }
}
