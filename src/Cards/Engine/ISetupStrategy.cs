namespace Cards.Engine;

/// <summary>
/// Creates players and expands zone definitions from a game definition into
/// a <see cref="GameState"/>.
///
/// Separating setup from game logic means every game gets consistent player
/// IDs, consistent zone expansion rules, and a single place to change if
/// that convention ever evolves.
///
/// The default implementation is <see cref="SetupEngine"/>.  Games that need
/// additional zones not expressible in the JSON definition should call
/// <see cref="SetupEngine.Instance"/> and then add their extra zones afterward.
/// </summary>
public interface ISetupStrategy
{
    /// <summary>
    /// Populates <paramref name="state"/> with players (count = <paramref name="playerCount"/>)
    /// and all zones declared in <c>state.Definition.Zones</c>.
    /// Zones with <c>owner: "each_player"</c> are expanded into one zone per player.
    /// </summary>
    void Setup(GameState state, int playerCount, IReadOnlyList<string> enabledRules);
}
