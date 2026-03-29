namespace Cards.Engine;

/// <summary>
/// Evaluates whether a game has ended and determines the winner.
/// </summary>
public interface IWinCondition
{
    /// <summary>
    /// Returns a <see cref="WinResult"/> if the win condition is satisfied;
    /// otherwise <c>null</c> (game continues).
    /// </summary>
    WinResult? Check(GameState state);

    /// <summary>
    /// Forces win resolution regardless of whether end conditions are met.
    /// Call when game logic has already determined the game must end
    /// (e.g., not enough cards remain to continue a war).
    /// </summary>
    WinResult Resolve(GameState state);
}

/// <param name="WinnerId">The winning player's ID, or <c>null</c> for a draw/tie.</param>
/// <param name="StatusMessage">Primary status text to display (e.g. "You win the game!").</param>
/// <param name="SubMessage">Optional secondary text (e.g. score summary).</param>
public sealed record WinResult(string? WinnerId, string StatusMessage, string? SubMessage = null);
