namespace Cards.Engine;

/// <summary>
/// Implemented by each game's logic module.  The module owns all state
/// mutations — the page/view layer only calls these methods.
/// </summary>
public interface IGameLogic
{
    /// <summary>
    /// Set up players, zones, and deal cards.  Called once after the
    /// GameState shell is created.
    /// </summary>
    void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules);

    /// <summary>Actions the current player may take right now.</summary>
    IReadOnlyList<GameAction> GetValidActions(GameState state);

    /// <summary>
    /// Apply an action, mutating <paramref name="state"/> in place.
    /// </summary>
    void Apply(GameState state, GameAction action);

    /// <summary>True when the game has ended.</summary>
    bool IsGameOver(GameState state);

    /// <summary>One-line prompt shown to the player (e.g. "Tap to flip!").</summary>
    string GetStatusText(GameState state);

    /// <summary>
    /// When non-null the page will automatically advance the game after this delay
    /// without waiting for user input.  Return null to require an explicit tap/button.
    /// </summary>
    TimeSpan? GetAutoAdvanceDelay(GameState state) => null;

    /// <summary>
    /// Card IDs the current player may tap or drag.  Shown with a selection highlight.
    /// Return an empty list when card selection is not applicable.
    /// </summary>
    IReadOnlyList<string> GetSelectableCardIds(GameState state) => [];

    /// <summary>
    /// Zone IDs that are valid drop targets when <paramref name="cardId"/> is being dragged
    /// or has been tap-selected.  Shown with a drop-zone highlight.
    /// </summary>
    IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId) => [];

    /// <summary>
    /// Returns the action that should be applied during an auto-advance tick.
    /// When the current player has a registered <see cref="IPlayerAgent"/> the agent
    /// picks from legal card-play or action choices; otherwise the first valid action
    /// is returned (preserving existing behaviour for purely scripted phases).
    /// </summary>
    GameAction GetAutoAction(GameState state)
    {
        var valid = GetValidActions(state);
        return valid.Count > 0 ? valid[0] : new GameAction("tap");
    }
}
