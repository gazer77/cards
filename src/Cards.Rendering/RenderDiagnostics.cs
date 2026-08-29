using SkiaSharp;

namespace Cards.Rendering;

/// <summary>
/// Frame timings and counters for the debug overlay.
///
/// Exists because animation smoothness has to be measured where it runs. The same
/// renderer is several times slower under WebAssembly than on the desktop that hosts
/// the benchmark, so a desktop number can point at the right cause and still be badly
/// wrong about the magnitude — and "it looks choppy" cannot tell you whether frames
/// are slow, uneven, or simply not being requested.
///
/// Deliberately separates the three:
///   <see cref="Fps"/>        — how often a frame actually reached the screen.
///   <see cref="PaintMs"/>    — how long this renderer spent drawing one.
///   <see cref="IntervalMs"/> — the gap between frames, which is the frame source.
///
/// A low FPS with a small paint time means the frames are not being asked for; a low
/// FPS with a large paint time means they cost too much. Those have opposite fixes.
/// </summary>
public sealed class RenderDiagnostics
{
    private const int Window = 60;

    private readonly double[] _paintMs = new double[Window];
    private readonly double[] _gapMs   = new double[Window];
    private int  _count;
    private int  _next;

    // Gaps are counted separately from paints. The first frame has no predecessor, so
    // it contributes a paint time but no interval; folding a zero in there would drag
    // the mean down and overstate the frame rate.
    private int  _gapCount;
    private int  _gapNext;
    private long _lastFrameStart;

    /// <summary>Cards drawn in the last frame — the renderer's main cost driver.</summary>
    public int CardsDrawn { get; set; }

    public int FlyIns   { get; set; }
    public int Deals    { get; set; }
    public int Flips    { get; set; }
    public int Receives { get; set; }
    public int Shuffles { get; set; }

    public SKImageInfo Info { get; set; }

    /// <summary>
    /// Total cards on the table, and where they are.
    ///
    /// Counting cards by eye is unreliable: a large hand is drawn as overlapping
    /// slices, so a 28-card hand and a 16-card one look more alike than they are. If a
    /// game appears to have lost cards, this answers it outright rather than by
    /// inference from the picture.
    /// </summary>
    public string CardCensus { get; set; } = "";

    /// <summary>Mean milliseconds spent painting, over the last <see cref="Window"/> frames.</summary>
    public double PaintMs => Mean(_paintMs, _count);

    /// <summary>Mean milliseconds between frames. Compare against PaintMs to see which dominates.</summary>
    public double IntervalMs => Mean(_gapMs, _gapCount);

    /// <summary>Frames per second, derived from the measured gap rather than assumed.</summary>
    public double Fps => IntervalMs > 0.01 ? 1000.0 / IntervalMs : 0;

    /// <summary>
    /// The frame rate this renderer could sustain if frames were requested the instant
    /// the previous one finished. Above the observed FPS means the frame source, not
    /// the drawing, is the limit.
    /// </summary>
    public double PaintCeilingFps => PaintMs > 0.01 ? 1000.0 / PaintMs : 0;

    public void BeginFrame(long nowMs)
    {
        if (_lastFrameStart != 0)
        {
            _gapMs[_gapNext] = nowMs - _lastFrameStart;
            _gapNext  = (_gapNext + 1) % Window;
            _gapCount = Math.Min(_gapCount + 1, Window);
        }

        _lastFrameStart = nowMs;
        CardsDrawn = 0;
    }

    public void EndFrame(double paintMs)
    {
        _paintMs[_next] = paintMs;
        _next  = (_next + 1) % Window;
        _count = Math.Min(_count + 1, Window);
    }

    private static double Mean(double[] values, int count)
    {
        if (count == 0) return 0;

        double sum = 0;
        for (int i = 0; i < count; i++) sum += values[i];
        return sum / count;
    }

    public IReadOnlyList<string> Lines() =>
    [
        $"{Fps,5:F1} fps   frame {IntervalMs,5:F1} ms",
        $"paint {PaintMs,5:F1} ms  (ceiling {PaintCeilingFps,4:F0} fps)",
        $"cards drawn {CardsDrawn}   cached {CardRenderer.CacheSize}",
        $"anim  fly {FlyIns}  deal {Deals}  flip {Flips}  recv {Receives}  shuf {Shuffles}",
        $"canvas {Info.Width}x{Info.Height}",
        CardCensus,
    ];
}
