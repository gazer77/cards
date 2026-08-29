using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Dumps what a game is actually offering at the point it stops progressing.
///
/// Written after three attempts to guess why several games appeared livelocked: each
/// guess changed the driver and measured the driver. This asks the engine instead —
/// which actions, which cards, which drop zones — so the next change is informed.
///
/// PROBE=hearts:4 dotnet test --filter StuckGameProbe
/// </summary>
public sealed class StuckGameProbe
{
    [Fact]
    public void Report()
    {
        var spec = Environment.GetEnvironmentVariable("PROBE");
        if (string.IsNullOrEmpty(spec)) return;

        var parts = spec.Split(':');
        string gameId = parts[0];
        int seats = parts.Length > 1 ? int.Parse(parts[1]) : 2;

        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var definition = loader.LoadAsync(gameId).GetAwaiter().GetResult()!;

        var state = new GameState
        {
            GameId = definition.Id, Definition = definition,
            Rng = new SeededRandomSource(17),
        };
        var logic = LogicRegistry.Create(definition);
        logic.Initialize(state, seats, []);

        int tap = 0;
        for (int step = 0; step < 40 && !logic.IsGameOver(state); step++)
        {
            var actions    = logic.GetValidActions(state);
            var selectable = logic.GetSelectableCardIds(state);
            var selected   = state.Metadata.GetValueOrDefault("selected_card", "");
            var drops      = selected.Length > 0 && !selected.Contains(',')
                ? logic.GetDropZoneIds(state, selected)
                : [];

            Console.WriteLine(
                $"[probe] {step,3} phase={state.CurrentPhaseId,-12} p{state.CurrentPlayerIndex} " +
                $"actions=[{string.Join(",", actions.Select(a => a.Type))}] " +
                $"selectable={selectable.Count} selected={(selected.Length == 0 ? "-" : selected)} " +
                $"drops=[{string.Join(",", drops)}] " +
                $"auto={logic.GetAutoAdvanceDelay(state)?.TotalMilliseconds.ToString() ?? "null"}");

            if (actions.Count > 0)
            {
                var auto = logic.GetAutoAction(state);
                Console.WriteLine($"[probe]      -> auto action: {auto.Type} card={auto.CardId} zone={auto.ZoneId}");
                logic.Apply(state, auto);
            }
            else if (drops.Count > 0)
            {
                logic.Apply(state, new GameAction("play_card", CardId: selected, ZoneId: drops[0]));
            }
            else if (selectable.Count > 0)
            {
                // Rotate. Several handlers toggle selection, so tapping one card over
                // and over selects and deselects it forever.
                logic.Apply(state, new GameAction("select_card", CardId: selectable[tap++ % selectable.Count]));
            }
            else
            {
                Console.WriteLine("[probe]      -> nothing on offer; stuck");
                break;
            }
        }
    }
}
