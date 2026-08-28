using System.Diagnostics;
using SkiaSharp;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Times a whole table paint, the same operation the in-game overlay reports.
///
/// Sized to match a real browser session (1920x766) so the number here and the number
/// on screen mean the same thing — with the caveat that WebAssembly measured roughly
/// 19x slower on identical work, so this is a lower bound, not a prediction.
///
/// Run with: PAINT_COST=1 dotnet test --filter TablePaintCost
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class TablePaintCostTests
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void Report_full_table_paint_cost()
    {
        if (Environment.GetEnvironmentVariable("PAINT_COST") is not ("1" or "true"))
            return;

        const int w = 1920, h = 766, frames = 60;

        var renderer = new CardTableRenderer(new StubDriver()) { GameState = TestTable.Build() };
        var info = new SKImageInfo(w, h);

        using var bitmap = new SKBitmap(w, h);
        using var canvas = new SKCanvas(bitmap);

        CardRenderer.ClearCache();
        renderer.Paint(canvas, info);   // warm the card and felt caches

        var clock = Stopwatch.StartNew();
        for (int i = 0; i < frames; i++)
            renderer.Paint(canvas, info);
        clock.Stop();

        double ms = clock.Elapsed.TotalMilliseconds / frames;
        Console.WriteLine(
            $"[table] {w}x{h}: {ms:F2} ms/frame => {1000.0 / ms:F0} fps ceiling on this machine.");
    }
}
