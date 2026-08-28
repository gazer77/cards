using Cards.Engine;

namespace Cards.App;

/// <summary>
/// Plays the table's card movement animations on behalf of the turn loop.
///
/// The engine deliberately never sleeps — it reports how long a step should take and
/// leaves the waiting to the driver. That covers the pause between turns, but not the
/// motion itself: a card sliding from the deck into a hand takes real time, and the
/// next turn must not start on top of it. Without this seam a client is left applying
/// state changes at the speed the CPU can compute them, which is exactly how the
/// browser client behaved before it existed.
///
/// Geometry lives in the renderer, so the implementation does too
/// (<c>Cards.Rendering.RendererTableAnimator</c>). This interface stays Skia-free so
/// <see cref="GameTableViewModel"/> can drive animations without knowing how the table
/// is painted — or whether it is painted at all.
/// </summary>
public interface ITableAnimator
{
    /// <summary>
    /// Records where every card currently sits. Must be called immediately *before*
    /// <c>IGameLogic.Apply</c>: once the action lands, a card's origin is gone.
    /// </summary>
    void CaptureBeforeMove(GameState state);

    /// <summary>
    /// Flies every card that changed zones from where it was to where it now belongs,
    /// completing when the motion has finished. Call after the view has been given the
    /// post-action state, so destinations resolve against the new layout.
    /// </summary>
    Task PlayMoveAsync(GameState state);

    /// <summary>
    /// Plays the opening choreography: a full deck, a riffle shuffle, then cards
    /// dealt out one at a time in the order the engine actually dealt them.
    /// </summary>
    Task PlayDealAsync(GameState state);
}

/// <summary>
/// Does nothing, instantly. The default, so a headless host — a test, or the relay
/// server running a game with no screen attached — needs no animation support.
/// </summary>
public sealed class NullTableAnimator : ITableAnimator
{
    public static readonly NullTableAnimator Instance = new();

    public void CaptureBeforeMove(GameState state) { }
    public Task PlayMoveAsync(GameState state) => Task.CompletedTask;
    public Task PlayDealAsync(GameState state) => Task.CompletedTask;
}
