namespace Cards.Engine;

/// <summary>
/// Fallback logic for games that carry no <c>implementation</c> key in their
/// definition.  Drives the game entirely from the JSON definition:
///
///   • SetupEngine creates players and zones.
///   • StandardDealEngine deals the cards.
///   • PhaseHandlerRegistry creates a handler for each declared phase by type.
///   • WinConditionEngine evaluates the win condition after every round.
///
/// Games that need custom C# provide a named implementation in LogicRegistry
/// instead; this class only runs when no such implementation is found.
/// </summary>
public sealed class DefaultGameLogic : GameLogicBase
{
    public override void Initialize(
        GameState state,
        int playerCount,
        IReadOnlyList<string> enabledHouseRules)
    {
        SetupEngine.Instance.Setup(state, playerCount, enabledHouseRules);
        StandardDealEngine.Instance.Deal(state, playerCount, enabledHouseRules);

        // Register a handler for each phase in the definition.
        // Each handler receives the ID of the next phase so it can advance the game.
        var phases = state.Definition.Phases;
        for (int i = 0; i < phases.Count; i++)
        {
            // Next phase wraps around to the first — the result handler cycles back to ready.
            string nextId   = phases[(i + 1) % phases.Count].Id;
            var    handler  = PhaseHandlerRegistry.Create(phases[i], nextId);
            if (handler is not null)
                RegisterPhase(phases[i].Id, handler);
        }

        string firstPhase = phases.FirstOrDefault()?.Id ?? "game_over";
        state.CurrentPhaseId = firstPhase;
        state.Metadata["status"] = "Tap to flip!";
    }
}
