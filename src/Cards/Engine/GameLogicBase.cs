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

    // ── Standard handlers ─────────────────────────────────────────────────────

    /// <summary>Terminal phase — no valid actions, no auto-advance.</summary>
    private sealed class GameOverPhaseHandler : IPhaseHandler
    {
        public static readonly GameOverPhaseHandler Instance = new();
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [];
        public void Apply(GameState state, GameAction action) { }
    }
}
