namespace Cards.Engine;

/// <summary>
/// Shuffles a deck and deals cards into player zones, returning a <see cref="DealResult"/>
/// that describes what was dealt and in what order.
///
/// The result is also stored on <see cref="GameState.LastDealResult"/> so the animation
/// layer can consume it without the page needing to reconstruct the deal sequence.
///
/// Implementations are selected by the game definition:
///   - No <c>implementation</c> key → <see cref="StandardDealEngine"/> (built-in default)
///   - Named key (e.g. <c>"blackjack"</c>) → registered custom strategy
///
/// See also: <see cref="IShuffleStrategy"/> for the shuffle step within a deal.
/// </summary>
public interface IDealStrategy
{
    /// <summary>
    /// Builds a fresh deck, shuffles it, deals cards to player zones, and returns
    /// the <see cref="DealResult"/>.  The deck zone must already exist in
    /// <paramref name="state"/> before this is called.
    /// </summary>
    DealResult Deal(GameState state, int playerCount, IReadOnlyList<string> enabledRules);
}
