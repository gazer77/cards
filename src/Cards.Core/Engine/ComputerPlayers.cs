namespace Cards.Engine;

/// <summary>How well the computer plays: <see cref="Easy"/>, <see cref="Normal"/> or <see cref="Hard"/>.</summary>
public static class Difficulty
{
    public const string Easy   = "easy";
    public const string Normal = "normal";
    public const string Hard   = "hard";

    public static readonly IReadOnlyList<string> All = [Easy, Normal, Hard];

    /// <summary>A known level, or normal for anything else — a save or a setting from before levels existed.</summary>
    public static string Of(string? level) => level is Easy or Hard ? level : Normal;
}

/// <summary>
/// Every computer seat comes from here, at the table's <see cref="GameState.Difficulty"/>.
/// Normal is the player the engine always had, so a game played at normal plays exactly
/// as before; hard plays the same game better; easy plays it worse, on purpose.
/// </summary>
public static class ComputerPlayers
{
    public static IPlayerAgent For(GameState state, string playerId)
        => Wrap(state, new SmartDefaultAiAgent(playerId, state.Rng) { Hard = state.Difficulty == Difficulty.Hard });

    /// <summary>A game's own player (Go Fish's), at the table's level.</summary>
    public static IPlayerAgent Wrap(GameState state, IPlayerAgent agent)
        => state.Difficulty == Difficulty.Easy ? new EasyPlayer(agent, state.Rng) : agent;
}

/// <summary>
/// Plays like the player it wraps, except that at a real decision — which card, which
/// bid, hit or stand — it often just picks one. Every choice it makes is legal; it is
/// only careless. Steps that are bookkeeping rather than decisions (picking up cards
/// to meld, confirming a choice already made) are left to the player it wraps, so a
/// careless turn still ends.
/// </summary>
public sealed class EasyPlayer(IPlayerAgent inner, IRandomSource rng) : IPlayerAgent
{
    /// <summary>How often a decision is made at random.</summary>
    public const double Carelessness = 0.45;

    public string PlayerId => inner.PlayerId;

    public GameAction ChooseAction(GameState visibleState, IReadOnlyList<GameAction> validActions)
    {
        var decisions = validActions.Where(IsDecision).ToList();
        if (decisions.Count > 1 && decisions.Count == validActions.Count && rng.Next(100) < Carelessness * 100)
            return decisions[rng.Next(decisions.Count)];
        return inner.ChooseAction(visibleState, validActions);
    }

    private static bool IsDecision(GameAction a)
        => a.Type is "play_card" or "hit" or "stand" or "double_down" or "split" or "surrender"
                   or "call" or "check" or "raise" or "fold" or "all_in"
        || a.Type.StartsWith("bid_") || a.Type.StartsWith("trump_") || a.Type.StartsWith("draw_from_");
}
