using SkiaSharp;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Renders whole tables for eyeballing — seat names, upright hands, the room left for
/// the footer. Opt in with TABLE_SHEET=1; SHEET_DIR says where the PNGs go. Assertions
/// cannot tell you whether a name is legible or sits where a person would look for it.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class TableSheet
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void Write_table_sheet()
    {
        if (Environment.GetEnvironmentVariable("TABLE_SHEET") is not ("1" or "true"))
            return;

        foreach (var (game, seats) in new[] { ("golf", 4), ("hearts", 4), ("blackjack", 3), ("golf", 6) })
        {
            var state = TestTable.Build(game, seats);
            var renderer = new CardTableRenderer(new StubDriver())
            {
                GameState   = state,
                BottomInset = 70,   // the web footer at 4.4rem on a 16px page
            };

            const int w = 950, h = 880;
            using var bitmap = new SKBitmap(w, h);
            using var canvas = new SKCanvas(bitmap);
            // Once to start the deal, then again after it has settled, so the sheet shows
            // where the cards rest rather than where they are sliding from.
            renderer.Paint(canvas, new SKImageInfo(w, h));
            Thread.Sleep(2500);
            renderer.Paint(canvas, new SKImageInfo(w, h));

            string path = Path.Combine(
                Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath(),
                $"table-{game}-{seats}p.png");

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file  = File.Create(path);
            data.SaveTo(file);
        }
    }
}
