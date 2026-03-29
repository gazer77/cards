using Cards.Logic;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Maps implementation keys to their IGameLogic factories.
///
/// The key is the <c>implementation</c> field from the game definition, which
/// defaults to the game's <c>id</c> when absent.  This decouples the game
/// definition ID (e.g. "euchre-4p") from the logic module (e.g. "euchre").
/// </summary>
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
    /// Returns a fresh logic instance driven by the definition's
    /// <c>implementation</c> key (falling back to <c>id</c>), or <c>null</c>
    /// when no matching module is registered.
    /// </summary>
    public static IGameLogic? Create(GameDefinition definition)
    {
        string key = definition.Implementation ?? definition.Id;
        return _factories.TryGetValue(key, out var factory) ? factory() : null;
    }

    /// <summary>
    /// Returns a fresh logic instance by raw key, or <c>null</c> when no
    /// matching module is registered.  Prefer <see cref="Create(GameDefinition)"/>
    /// when a definition is available.
    /// </summary>
    public static IGameLogic? Create(string implementationKey)
        => _factories.TryGetValue(implementationKey, out var factory) ? factory() : null;
}
