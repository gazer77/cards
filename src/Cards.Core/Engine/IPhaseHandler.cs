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

    /// <summary>
    /// The one obvious thing to do with this card — what a double tap on it means.
    ///
    /// Null when this phase has no such thing, and the double tap is then just a tap.
    /// A phase that asks before it acts (see the confirm parameters) answers here with
    /// the action it would have taken, so a player who knows their mind can skip the
    /// asking without losing it for everyone else.
    /// </summary>
    GameAction? DefaultCardAction(GameState state, string cardId, int? uid) => null;

    /// <summary>
    /// Called once when this phase becomes the current one, before anything else is
    /// asked of it — in particular before the client asks whose turn it is.
    ///
    /// A phase that decides who acts first (bidding starts left of the dealer, a reveal
    /// starts at seat 0) used to do it inside GetValidActions, which is the second
    /// question a client asks. The first is "is this a person's turn or an agent's", and
    /// that was answered about whoever the previous phase happened to leave current: a
    /// person was shown Order Up and Pass while the bid belonged to the seat beside them.
    /// </summary>
    void OnPhaseEnter(GameState state) { }
    void OnGameStart(GameState state) { }
}
