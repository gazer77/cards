using Cards.Rendering;

namespace Cards.Web.Platform;

/// <summary>
/// Supplies animation frames in the browser.
///
/// Runs a ~60fps loop only while the renderer says animations are in flight, and stops
/// as soon as they drain — so the cost is bounded to the length of an animation
/// (typically well under two seconds) rather than running for the whole session.
///
/// requestAnimationFrame would be the better source: it pauses in a hidden tab and
/// syncs to the display refresh. That needs a JS module plus a [JSInvokable] callback,
/// and swapping it in later is a change to this class alone — which is why
/// <see cref="IAnimationDriver"/> exists.
/// </summary>
public sealed class BrowserAnimationDriver : IAnimationDriver, IDisposable
{
    private CancellationTokenSource? _cts;

    public event Action? Tick;

    public void RequestFrames()
    {
        if (_cts is not null) return;   // already running

        _cts = new CancellationTokenSource();
        _ = PumpAsync(_cts.Token);
    }

    public void StopFrames()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(16, ct);
                if (ct.IsCancellationRequested) break;
                Tick?.Invoke();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown when animations finish.
        }
    }

    public void Dispose() => StopFrames();
}
