using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Maps implementation keys to their IGameLogic factories.
///
/// The key is the <c>implementation</c> field from the game definition, which
/// defaults to the game's <c>id</c> when absent.  This decouples the game
/// definition ID (e.g. "euchre-4p") from the logic module (e.g. "euchre").
///
/// War, Blackjack, and Go Fish are now fully declarative — they no longer
/// require a custom logic class and have no entry here.
/// </summary>
public static class LogicRegistry
{
    private static readonly Dictionary<string, Func<IGameLogic>> _factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // All built-in games now run through DefaultGameLogic + PhaseHandlerRegistry.
            // Add entries here only when a game needs custom C# beyond what the
            // declarative engine provides.
        };

    /// <summary>
    /// Returns a fresh logic instance driven by the definition's
    /// <c>implementation</c> key (falling back to <c>id</c>).
    /// Returns a <see cref="DefaultGameLogic"/> for definitions that have no
    /// registered implementation — the engine will drive those games from the
    /// JSON definition alone.
    /// </summary>
    public static IGameLogic Create(GameDefinition definition)
    {
        string key = definition.Implementation ?? definition.Id;
        return _factories.TryGetValue(key, out var factory)
            ? factory()
            : new DefaultGameLogic();
    }

    /// <summary>
    /// Returns a fresh logic instance by raw key, or <c>null</c> when no
    /// matching module is registered.  Prefer <see cref="Create(GameDefinition)"/>
    /// when a definition is available.
    /// </summary>
    public static IGameLogic? Create(string implementationKey)
        => _factories.TryGetValue(implementationKey, out var factory) ? factory() : null;
}
