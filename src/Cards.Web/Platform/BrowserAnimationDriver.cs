using Microsoft.JSInterop;
using Cards.Rendering;

namespace Cards.Web.Platform;

/// <summary>
/// Supplies animation frames in the browser, from requestAnimationFrame.
///
/// The frame source matters more than it looks. Card motion is computed from
/// wall-clock time, so an irregular frame source does not change an animation's speed
/// or duration — it just samples it at uneven moments, which is seen as judder. A
/// timer loop (the original implementation used <c>Task.Delay(16)</c>) runs on the
/// browser's timer queue, independently of when the page actually repaints; rAF is
/// the display's own clock.
///
/// Frames run only while the renderer reports animations in flight, so the cost is
/// bounded to the length of an animation rather than the whole session — and rAF
/// additionally stops on its own in a hidden tab.
/// </summary>
public sealed class BrowserAnimationDriver : IAnimationDriver, IDisposable
{
    private readonly IJSRuntime _js;

    private DotNetObjectReference<BrowserAnimationDriver>? _self;
    private IJSObjectReference? _module;
    private int  _loopId;
    private bool _running;
    private bool _disposed;

    /// <summary>Drives frames when the JS module is unavailable. See <see cref="StartAsync"/>.</summary>
    private CancellationTokenSource? _fallback;

    public event Action? Tick;

    public BrowserAnimationDriver(IJSRuntime js) => _js = js;

    public void RequestFrames()
    {
        if (_running || _disposed) return;

        _running = true;
        _ = StartAsync();
    }

    public void StopFrames()
    {
        if (!_running) return;
        _running = false;

        _fallback?.Cancel();
        _fallback?.Dispose();
        _fallback = null;

        if (_module is not null && _loopId != 0)
        {
            int id = _loopId;
            _loopId = 0;
            _ = StopLoopAsync(id);
        }
    }

    /// <summary>Called from JS once per animation frame.</summary>
    [JSInvokable]
    public void OnFrame()
    {
        if (_running) Tick?.Invoke();
    }

    private async Task StartAsync()
    {
        try
        {
            _module ??= await _js.InvokeAsync<IJSObjectReference>(
                "import", "./js/frames.js");
            _self ??= DotNetObjectReference.Create(this);

            // StopFrames may have run while the module was loading.
            if (!_running) return;

            _loopId = await _module.InvokeAsync<int>("start", _self);
        }
        catch
        {
            // Falling back to a timer gives worse-looking motion, but the alternative
            // is a table whose animations never advance and therefore never complete —
            // and the turn loop waits on completion.
            if (_running) StartFallback();
        }
    }

    private void StartFallback()
    {
        _fallback = new CancellationTokenSource();
        var token = _fallback.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(16, token);
                    if (!token.IsCancellationRequested && _running) Tick?.Invoke();
                }
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }, token);
    }

    private async Task StopLoopAsync(int id)
    {
        try
        {
            if (_module is not null) await _module.InvokeVoidAsync("stop", id);
        }
        catch
        {
            // The page is going away — nothing left to stop.
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        StopFrames();
        _self?.Dispose();
        _self = null;
    }
}
