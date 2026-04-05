namespace Cards.Engine;

/// <summary>
/// Base class for game logic modules.  Replaces the manual switch-on-phase pattern
/// in <see cref="IGameLogic.Apply"/> / <see cref="IGameLogic.GetValidActions"/> with
/// a dictionary of <see cref="IPhaseHandler"/> objects, one per phase ID.
///
/// Subclasses call <see cref="RegisterPhase"/> during
/// <see cref="Initialize"/> to wire up their handlers.
/// The <c>game_over</c> phase is pre-registered with a no-op handler.
/// </summary>
public abstract class GameLogicBase : IGameLogic
{
    private readonly Dictionary<string, IPhaseHandler> _handlers = new();

    protected GameLogicBase()
    {
        RegisterPhase("game_over", GameOverPhaseHandler.Instance);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    protected void RegisterPhase(string phaseId, IPhaseHandler handler)
        => _handlers[phaseId] = handler;

    /// <summary>
    /// Calls <see cref="IPhaseHandler.OnGameStart"/> on the handler registered
    /// for <paramref name="phaseId"/>.  Used by <see cref="DefaultGameLogic"/>
    /// to let the first-phase handler perform initialization (e.g. a custom deal).
    /// </summary>
    protected void CallOnGameStart(string phaseId, GameState state)
    {
        if (_handlers.TryGetValue(phaseId, out var h))
            h.OnGameStart(state);
    }

    // ── IGameLogic ────────────────────────────────────────────────────────────

    public abstract void Initialize(
        GameState state,
        int playerCount,
        IReadOnlyList<string> enabledHouseRules);

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
        => _handlers.TryGetValue(state.CurrentPhaseId, out var h)
            ? h.GetValidActions(state)
            : [];

    public void Apply(GameState state, GameAction action)
    {
        if (_handlers.TryGetValue(state.CurrentPhaseId, out var h))
            h.Apply(state, action);
    }

    public virtual bool IsGameOver(GameState state)
        => state.CurrentPhaseId == "game_over";

    public virtual string GetStatusText(GameState state)
        => state.Metadata.GetValueOrDefault("status", "");

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
        => _handlers.TryGetValue(state.CurrentPhaseId, out var h)
            ? h.GetAutoAdvanceDelay(state)
            : null;

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
        => _handlers.TryGetValue(state.CurrentPhaseId, out var h)
            ? h.GetSelectableCardIds(state)
            : [];

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
        => _handlers.TryGetValue(state.CurrentPhaseId, out var h)
            ? h.GetDropZoneIds(state, cardId)
            : [];

    // ── Dealer helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns the initial dealer from the game definition's
    /// <c>rounds.first_dealer</c> setting.  Falls back to player 0.
    /// </summary>
    protected static void AssignInitialDealer(GameState state)
    {
        if (state.Players.Count == 0) return;
        var firstDealer = state.Definition.Rounds?.FirstDealer ?? "random";
        int idx = firstDealer == "random"
            ? new Random().Next(state.Players.Count)
            : 0;
        state.DealerId = state.Players[idx].Id;
    }

    /// <summary>
    /// Rotates the dealer seat according to <c>rounds.dealer</c>.
    /// Stores the winning/losing player ID in <c>metadata["last_winner"]</c> and
    /// <c>metadata["last_loser"]</c> when those rotation modes are used.
    /// </summary>
    protected static void RotateDealer(GameState state)
    {
        if (state.Players.Count == 0) return;
        string mode = state.Definition.Rounds?.Dealer ?? "rotates_left";

        int current = state.DealerId is null ? 0
            : state.Players.FindIndex(p => p.Id == state.DealerId);
        if (current < 0) current = 0;

        int next = mode switch
        {
            "rotates_right" => (current - 1 + state.Players.Count) % state.Players.Count,
            "winner"        => FindPlayerIndex(state, "last_winner", current),
            "loser"         => FindPlayerIndex(state, "last_loser",  current),
            "alternates"    => (current + state.RoundNumber) % state.Players.Count,
            _               => (current + 1) % state.Players.Count,  // rotates_left
        };

        state.DealerId = state.Players[next].Id;
    }

    private static int FindPlayerIndex(GameState state, string metaKey, int fallback)
    {
        if (!state.Metadata.TryGetValue(metaKey, out var id)) return fallback;
        int idx = state.Players.FindIndex(p => p.Id == id);
        return idx < 0 ? fallback : idx;
    }

    // ── Standard handlers ─────────────────────────────────────────────────────

    /// <summary>Terminal phase — no valid actions, no auto-advance.</summary>
    private sealed class GameOverPhaseHandler : IPhaseHandler
    {
        public static readonly GameOverPhaseHandler Instance = new();
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [];
        public void Apply(GameState state, GameAction action) { }
    }
}
