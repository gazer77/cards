namespace Cards.Rendering;

/// <summary>
/// Supplies animation frames to <see cref="CardTableRenderer"/>.
///
/// The renderer asks for frames while animations are running and says when they have
/// drained; the host decides how to produce them — a dispatcher timer on MAUI,
/// requestAnimationFrame in a browser, or a manual pump in tests.
/// </summary>
public interface IAnimationDriver
{
    /// <summary>Begin (or continue) raising <see cref="Tick"/> at roughly 60fps.</summary>
    void RequestFrames();

    /// <summary>Stop raising <see cref="Tick"/>; all animations have finished.</summary>
    void StopFrames();

    /// <summary>Raised once per frame while frames are running.</summary>
    event Action Tick;
}
