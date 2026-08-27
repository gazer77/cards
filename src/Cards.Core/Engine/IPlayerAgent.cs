namespace Cards.Engine;

/// <summary>
/// Strategy interface for AI-controlled players.
///
/// The engine calls <see cref="ChooseAction"/> with a masked view of the game
/// state — zones not visible to this player's seat have their card lists
/// cleared — so implementations see only what a human at that seat would see.
/// </summary>
public interface IPlayerAgent
{
    /// <summary>The player ID this agent controls (e.g. "player1").</summary>
    string PlayerId { get; }

    /// <summary>
    /// Returns the action the agent wants to take.
    /// </summary>
    /// <param name="visibleState">
    /// A masked snapshot of the game: hidden zones are empty, but zone counts
    /// are available via <c>Metadata["zone_count:{zoneId}"]</c>.
    /// </param>
    /// <param name="validActions">Legal action types the engine will accept.</param>
    GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions);
}
