using SkiaSharp;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Renders a table with speech bubbles for eyeballing. Opt in with BUBBLE_SHEET=1.
/// Coverage assertions cannot tell you whether a bubble points at the right seat or
/// runs off the edge of the table.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class BubbleSheet
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void Write_bubble_sheet()
    {
        if (Environment.GetEnvironmentVariable("BUBBLE_SHEET") is not ("1" or "true"))
            return;

        foreach (int seats in new[] { 2, 4 })
        {
            var state = TestTable.Build("hearts", seats);
            var renderer = new CardTableRenderer(new StubDriver()) { GameState = state };

            const int w = 1200, h = 800;
            using var bitmap = new SKBitmap(w, h);
            using var canvas = new SKCanvas(bitmap);

            // Paint once so zone layouts exist for the bubbles to anchor against.
            renderer.Paint(canvas, new SKImageInfo(w, h));

            foreach (var p in state.Players)
                renderer.PostMessage(p.Id, $"{p.Name} plays the queen of spades");

            renderer.Paint(canvas, new SKImageInfo(w, h));

            string path = Path.Combine(
                Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath(),
                $"bubbles-{seats}p.png");

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file  = File.OpenWrite(path);
            data.SaveTo(file);
        }
    }
}
