// Frame source for card animations.
//
// requestAnimationFrame is the only clock that is actually in step with the
// display. A timer loop (setTimeout, or .NET's Task.Delay) fires on the timer
// queue instead, so frames land slightly early or late relative to each repaint
// and some get coalesced away — which reads as jerky motion even though the
// animation itself is computed from wall-clock time and is perfectly smooth.
//
// It also pauses in a hidden tab, so a backgrounded game stops burning frames.

const loops = new Map();
let nextId = 1;

export function start(handler) {
    const id = nextId++;

    const step = () => {
        // Schedule the next frame before invoking, so a throw in the callback
        // cannot silently kill the loop.
        if (loops.has(id)) loops.set(id, requestAnimationFrame(step));
        handler.invokeMethod('OnFrame');
    };

    loops.set(id, requestAnimationFrame(step));
    return id;
}

export function stop(id) {
    const handle = loops.get(id);
    if (handle === undefined) return;

    cancelAnimationFrame(handle);
    loops.delete(id);
}
