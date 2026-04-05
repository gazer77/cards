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
/// Multi-round games declare a <c>rounds</c> block in the definition and route
/// their final phase's <c>next</c> to <c>"new_round"</c>.  The engine then
/// automatically checks the win condition; if not met it rotates the dealer,
/// clears per-round zones, and re-deals for the next round.
///
/// Games that need custom C# provide a named implementation in LogicRegistry
/// instead; this class only runs when no such implementation is found.
/// </summary>
public sealed class DefaultGameLogic : GameLogicBase
{
    private int _playerCount;
    private IReadOnlyList<string> _enabledHouseRules = [];
    private string _firstPhaseId = "game_over";

    public override void Initialize(
        GameState state,
        int playerCount,
        IReadOnlyList<string> enabledHouseRules)
    {
        _playerCount       = playerCount;
        _enabledHouseRules = enabledHouseRules;

        // Apply house rule overrides to a definition clone before any setup runs.
        state.Definition = HouseRuleEngine.Apply(state.Definition, enabledHouseRules);

        SetupEngine.Instance.Setup(state, playerCount, enabledHouseRules);

        if (state.Definition.Rounds is not null)
            AssignInitialDealer(state);

        StandardDealEngine.Instance.Deal(state, playerCount, enabledHouseRules);

        RegisterAllPhases(state);

        // Register DefaultAiAgent for every non-human seat that doesn't already have one.
        for (int i = 1; i < state.Players.Count; i++)
        {
            var pid = state.Players[i].Id;
            if (!state.PlayerAgents.ContainsKey(pid))
                state.PlayerAgents[pid] = new DefaultAiAgent(pid);
        }

        CallOnGameStart(_firstPhaseId, state);

        state.CurrentPhaseId     = _firstPhaseId;
        state.Metadata["status"] = "Tap to flip!";
    }

    // ── Phase registration ────────────────────────────────────────────────────

    private void RegisterAllPhases(GameState state)
    {
        var phases = state.Definition.Phases;
        bool hasRounds = state.Definition.Rounds is not null;

        for (int i = 0; i < phases.Count; i++)
        {
            // Determine the next phase ID for this handler.
            // If rounds are enabled the last phase wraps to "new_round";
            // otherwise it wraps back to the first phase (for single-round loops).
            string defaultNext = (i + 1 < phases.Count)
                ? phases[i + 1].Id
                : (hasRounds ? "new_round" : phases[0].Id);

            // Respect an explicit "next" in the phase definition when present.
            string nextId  = phases[i].NextPhase ?? defaultNext;
            var    handler = PhaseHandlerRegistry.Create(phases[i], nextId);
            if (handler is not null)
                RegisterPhase(phases[i].Id, handler);
        }

        _firstPhaseId = phases.FirstOrDefault()?.Id ?? "game_over";

        if (hasRounds)
            RegisterPhase("new_round", new NewRoundHandler(this));

    }

    // ── New-round handler ─────────────────────────────────────────────────────

    private sealed class NewRoundHandler(DefaultGameLogic logic) : IPhaseHandler
    {
        // Auto-advance immediately — the round transition happens without user input.
        public TimeSpan? GetAutoAdvanceDelay(GameState _) => TimeSpan.FromMilliseconds(600);
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

        public void Apply(GameState state, GameAction action)
        {
            // Check win condition before starting the next round.
            var win = WinConditionEngine.Instance.Check(state);
            if (win is not null)
            {
                state.Metadata["status"]      = win.StatusMessage;
                state.Metadata["last_winner"] = win.WinnerId ?? "";
                state.CurrentPhaseId          = "game_over";
                return;
            }

            // Not over — start the next round.
            state.RoundNumber++;
            RotateDealer(state);
            ClearRoundZones(state);

            StandardDealEngine.Instance.Deal(state, logic._playerCount, logic._enabledHouseRules);

            // Clear per-round tracking metadata.
            state.Metadata.Remove("trick_leader");
            state.Metadata.Remove("trick_trump");
            state.Metadata.Remove("trick_lead_suit");
            state.Metadata.Remove("trick_hearts_broken");
            state.Metadata.Remove("trick_spades_broken");
            state.Metadata.Remove("trick_number");
            state.Metadata.Remove("euchre_maker");
            state.Metadata.Remove("bid_winner");
            state.Metadata.Remove("bid_trump");
            state.Metadata.Remove("dd_knock_player");
            state.Metadata.Remove("dd_gin");
            state.Metadata.Remove("dd_go_out_player");
            state.Metadata.Remove("dd_go_out_team");
            state.Metadata.Remove("pot");
            foreach (var p in state.Players)
            {
                state.Metadata.Remove($"tricks_taken:{p.Id}");
                state.Metadata.Remove($"bid:{p.Id}");
                state.Metadata.Remove($"bid_alone:{p.Id}");
                state.Metadata.Remove($"bet_folded:{p.Id}");
                state.Metadata.Remove($"bet_all_in:{p.Id}");
                state.Metadata.Remove($"meld_score:{p.Id}");
            }

            state.CurrentPhaseId     = logic._firstPhaseId;
            state.Metadata["status"] = $"Round {state.RoundNumber}";
        }

        /// <summary>
        /// Clears all zones that should reset each round: hands, play zones, trick zones.
        /// Score zones (type "pot") and won_tricks piles are left intact.
        /// </summary>
        private static void ClearRoundZones(GameState state)
        {
            foreach (var zone in state.Zones.Values)
            {
                if (zone.Type is "hand" or "spread" or "trick" or "pile" or "deck")
                    zone.Clear();
            }
        }
    }
}
