using SkiaSharp;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Renders an opponent hand at several sizes, to confirm a large hand is drawn in full
/// rather than silently truncated. FAN_SHEET=1.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class FanDensitySheet
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void Write_fan_sheet()
    {
        if (Environment.GetEnvironmentVariable("FAN_SHEET") is not ("1" or "true")) return;

        foreach (int count in new[] { 16, 28 })
        {
            var state = TestTable.Build("go-fish", 2);

            // Pile the requested number of cards into the opponent's hand.
            var opponent = state.Zones[$"hand:{state.Players[1].Id}"];
            var all = state.Zones.Values.SelectMany(z => z.Cards).ToList();
            foreach (var z in state.Zones.Values) z.Clear();
            foreach (var c in all.Take(count)) { c.IsFaceUp = false; opponent.Add(c); }

            var renderer = new CardTableRenderer(new StubDriver()) { GameState = state };

            const int w = 1900, h = 900;
            using var bitmap = new SKBitmap(w, h);
            using var canvas = new SKCanvas(bitmap);
            renderer.Paint(canvas, new SKImageInfo(w, h));

            string path = Path.Combine(
                Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath(),
                $"fan-{count}.png");

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file  = File.OpenWrite(path);
            data.SaveTo(file);
        }
    }
}
