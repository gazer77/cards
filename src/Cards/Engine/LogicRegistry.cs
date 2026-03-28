using Cards.Logic;

namespace Cards.Engine;

/// <summary>Maps game IDs to their IGameLogic implementations.</summary>
public static class LogicRegistry
{
    private static readonly Dictionary<string, Func<IGameLogic>> _factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["war"]       = () => new WarLogic(),
            ["blackjack"] = () => new BlackjackLogic(),
            ["go-fish"]   = () => new GoFishLogic(),
        };

    /// <summary>
    /// Returns a fresh logic instance for the given game ID,
    /// or null if no logic module has been registered for that game.
    /// </summary>
    public static IGameLogic? Create(string gameId)
        => _factories.TryGetValue(gameId, out var factory) ? factory() : null;
}
