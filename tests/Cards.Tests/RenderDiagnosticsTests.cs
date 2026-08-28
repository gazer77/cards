using SkiaSharp;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Covers the debug overlay's own numbers.
///
/// A diagnostic that lies is worse than none: it sends whoever reads it after the
/// wrong cause. These check the arithmetic that separates "frames are slow" from
/// "frames are not being asked for" — the distinction the overlay exists to make.
/// </summary>
public sealed class RenderDiagnosticsTests
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
        public void Fire() => Tick?.Invoke();
    }

    [Fact]
    public void Fps_is_derived_from_the_gap_between_frames_not_from_paint_time()
    {
        var d = new RenderDiagnostics();

        // Frames 100 ms apart, each costing 5 ms to paint: 10 fps observed, but the
        // renderer could sustain 200. That gap is the whole point of the overlay —
        // it says the frame source is the limit, not the drawing.
        long t = 0;
        for (int i = 0; i < 10; i++)
        {
            d.BeginFrame(t);
            d.EndFrame(5.0);
            t += 100;
        }

        Assert.InRange(d.Fps, 9.0, 11.0);
        Assert.InRange(d.PaintCeilingFps, 150.0, 250.0);
    }

    [Fact]
    public void Paint_bound_rendering_shows_a_ceiling_near_the_observed_rate()
    {
        var d = new RenderDiagnostics();

        // Frames 100 ms apart, each costing 95 ms: the drawing itself is the limit.
        long t = 0;
        for (int i = 0; i < 10; i++)
        {
            d.BeginFrame(t);
            d.EndFrame(95.0);
            t += 100;
        }

        Assert.InRange(d.Fps, 9.0, 11.0);
        Assert.InRange(d.PaintCeilingFps, 9.0, 12.0);
    }

    [Fact]
    public void Reports_nothing_rather_than_dividing_by_zero_before_any_frames()
    {
        var d = new RenderDiagnostics();

        Assert.Equal(0, d.Fps);
        Assert.Equal(0, d.PaintCeilingFps);
        Assert.NotEmpty(d.Lines());
    }

    [Fact]
    public void Counts_the_cards_a_frame_actually_painted()
    {
        var renderer = new CardTableRenderer(new StubDriver()) { ShowDiagnostics = true };

        var state = TestTable.Build();
        renderer.GameState = state;

        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        renderer.Paint(canvas, new SKImageInfo(900, 600));

        // The exact number depends on layout, but a table holding cards must paint
        // some — a zero here means the counter is not wired to the draw calls and
        // the overlay's main cost figure is fiction.
        Assert.True(renderer.Diagnostics.CardsDrawn > 0,
            "Painted a table with cards but counted none.");
    }

    [Fact]
    public void Counts_nothing_when_the_overlay_is_off()
    {
        var renderer = new CardTableRenderer(new StubDriver());
        renderer.GameState = TestTable.Build();

        using var bitmap = new SKBitmap(900, 600);
        using var canvas = new SKCanvas(bitmap);
        renderer.Paint(canvas, new SKImageInfo(900, 600));

        // Timings must not accumulate while off: the overlay holds the frame loop
        // open, so leaving it measuring would cost frames in normal play.
        Assert.Equal(0, renderer.Diagnostics.Fps);
    }
}
