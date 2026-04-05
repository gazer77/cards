namespace Cards.Engine;

/// <summary>
/// Fallback AI agent used for any AI seat that doesn't have a game-specific
/// strategy registered.  Selects randomly from the available actions or cards.
/// </summary>
public sealed class DefaultAiAgent : IPlayerAgent
{
    private readonly Random _rng = new();

    public string PlayerId { get; }

    public DefaultAiAgent(string playerId) => PlayerId = playerId;

    public GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions)
    {
        if (validActions.Count == 0) return new GameAction("tap");
        return validActions[_rng.Next(validActions.Count)];
    }
}
