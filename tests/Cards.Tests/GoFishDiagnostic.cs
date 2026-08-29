using Cards.Engine;

namespace Cards.Tests;

/// <summary>Reports how a Go Fish game actually plays out. GOFISH=1 to run.</summary>
public sealed class GoFishDiagnostic
{
    [Fact]
    public void Report()
    {
        if (Environment.GetEnvironmentVariable("GOFISH") is not ("1" or "true")) return;

        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync("go-fish").GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(7),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, 2, []);

        int asks = 0, hits = 0, steps = 0;
        string lastStatus = "";

        while (steps++ < 2000 && !logic.IsGameOver(state))
        {
            var actions = logic.GetValidActions(state);
            if (actions.Count > 0)
                logic.Apply(state, logic.GetAutoAction(state));
            else
            {
                var sel = logic.GetSelectableCardIds(state);
                if (sel.Count == 0) break;
                logic.Apply(state, new GameAction("select_card", CardId: sel[0]));
            }

            string status = state.Metadata.GetValueOrDefault("status", "");
            if (status != lastStatus)
            {
                lastStatus = status;
                if (status.Contains("asked for") || status.Contains("Got ") || status.Contains("Go Fish"))
                {
                    asks++;
                    if (status.Contains("Got ") || status.Contains("got ")) hits++;
                }
                if (asks <= 14) Console.WriteLine($"[gf] {status}");
            }
        }

        Console.WriteLine($"[gf] --- {asks} asks, {hits} succeeded, {steps} steps, " +
                          $"books p0={state.GetScore(state.Players[0].Id)} " +
                          $"p1={state.GetScore(state.Players[1].Id)}, over={logic.IsGameOver(state)}");
    }
}
