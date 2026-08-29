using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// The same rank census, but with house rules on and with the player choosing cards
/// unpredictably rather than always tapping the same end of their hand.
///
/// A harness that always taps the first card explores one narrow path through the
/// game; a person does not.
/// </summary>
public sealed class GoFishHouseRuleCensus
{
    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("GOFISH") is not ("1" or "true")) return;

        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        foreach (var rules in new IReadOnlyList<string>[]
                 { [], ["pairs"], ["seven_cards_all"], ["pairs", "seven_cards_all"] })
        {
            int finished = 0, stalls = 0, census = 0;
            string label = rules.Count == 0 ? "(none)" : string.Join("+", rules);

            for (ulong seed = 1; seed <= 120; seed++)
            {
                var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;
                var state = new GameState
                {
                    GameId = definition.Id, Definition = definition,
                    Rng = new SeededRandomSource(seed),
                };
                var logic = LogicRegistry.Create(definition);
                logic.Initialize(state, 2, rules);

                var rng = new Random((int)seed);

                for (int step = 0; step < 4000 && !logic.IsGameOver(state); step++)
                {
                    var actions = logic.GetValidActions(state);
                    if (actions.Count > 0)
                    {
                        // Prefer asking over cancelling, but otherwise play like a person.
                        var act = actions.FirstOrDefault(a => a.Type == "ask")
                                  ?? logic.GetAutoAction(state);
                        logic.Apply(state, act);
                    }
                    else
                    {
                        var sel = logic.GetSelectableCardIds(state);
                        if (sel.Count == 0) break;
                        logic.Apply(state, new GameAction("select_card", CardId: sel[rng.Next(sel.Count)]));
                    }

                    var bad = state.Zones.Values.SelectMany(z => z.Cards)
                        .GroupBy(c => c.Rank).Where(g => g.Count() != 4).ToList();

                    if (bad.Count > 0)
                    {
                        census++;
                        Console.WriteLine($"[census] {label} seed {seed} step {step}: " +
                            string.Join(", ", bad.Select(g => $"{g.Key}x{g.Count()}")));
                        break;
                    }
                }

                if (logic.IsGameOver(state)) finished++;
                else
                {
                    stalls++;
                    if (stalls <= 2)
                        Console.WriteLine($"[stall] {label} seed {seed}: deck " +
                            $"{state.Zones["deck"].Count}, " +
                            string.Join(", ", state.Zones
                                .Where(z => z.Key.StartsWith("hand:") || z.Key.StartsWith("books:"))
                                .Select(z => $"{z.Key}={z.Value.Count}")));
                }
            }

            Console.WriteLine($"[gf] rules {label}: {finished} finished, {stalls} stalled, {census} census failures");
        }
    }
}
