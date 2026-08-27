namespace Cards.Engine;

/// <summary>
/// Handles all logic for a single named phase of a game.
///
/// Game logic classes register one IPhaseHandler per phase ID and delegate
/// GetValidActions / Apply / GetAutoAdvanceDelay / GetSelectableCardIds /
/// GetDropZoneIds to whichever handler owns the current phase.
/// </summary>
public interface IPhaseHandler
{
    /// <summary>Actions the current player may take in this phase.</summary>
    IReadOnlyList<GameAction> GetValidActions(GameState state);

    /// <summary>Apply an action, mutating <paramref name="state"/> in place.</summary>
    void Apply(GameState state, GameAction action);

    /// <summary>
    /// When non-null the page will automatically advance after this delay
    /// without waiting for user input.
    /// </summary>
    TimeSpan? GetAutoAdvanceDelay(GameState state) => null;

    /// <summary>Card IDs the player may tap or drag in this phase.</summary>
    IReadOnlyList<string> GetSelectableCardIds(GameState state) => [];

    /// <summary>
    /// Zone IDs that are valid drop targets when <paramref name="cardId"/> is
    /// being dragged or has been tap-selected in this phase.
    /// </summary>
    IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId) => [];

    /// <summary>
    /// Called once by DefaultGameLogic after all phases are registered, before
    /// the first action.  Use this to perform initialization that must run during
    /// <see cref="IGameLogic.Initialize"/> — e.g. custom deal sequences that set
    /// <see cref="GameState.LastDealResult"/> for the animation layer.
    /// Only invoked on the handler for the game's first phase.
    /// Default implementation is a no-op.
    /// </summary>
    void OnGameStart(GameState state) { }
}
