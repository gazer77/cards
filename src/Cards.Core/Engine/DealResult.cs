namespace Cards.Engine;

/// <summary>
/// Produced by <see cref="IDealStrategy.Deal"/> after cards are shuffled and dealt.
/// Carries the authoritative deal sequence so the animation layer can mirror exactly
/// what the engine did — no reconstruction or guessing required.
/// </summary>
public sealed class DealResult
{
    /// <summary>
    /// Maps player index → ordered list of card uids in that player's hand after dealing.
    /// </summary>
    public required Dictionary<int, List<int>> CardsByPlayerIndex { get; init; }

    /// <summary>
    /// The deal sequence as (PlayerIndex, Count) steps.
    /// Each step represents dealing <c>Count</c> consecutive cards to <c>PlayerIndex</c>.
    /// Steps are in the order cards were physically dealt.
    /// </summary>
    public required IReadOnlyList<(int PlayerIndex, int Count)> Steps { get; init; }

    /// <summary>
    /// Milliseconds between each individual card animation.
    /// Sourced from <c>anim_delay_ms</c> in the deal definition; defaults to 130.
    /// </summary>
    public int AnimDelayMs { get; init; } = 130;
}
