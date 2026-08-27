using SkiaSharp;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Writes a contact sheet of rendered cards to disk for eyeballing.
///
/// Not an assertion — the automated checks live in <see cref="CardRenderingTests"/>.
/// This exists because coverage thresholds tell you a suit painted *something*, not
/// whether it looks like a spade. Opt in with:
///     RENDER_SHEET=1 dotnet test --filter VisualSheet
/// </summary>
public sealed class VisualSheet
{
    [Fact]
    public void Write_contact_sheet()
    {
        if (Environment.GetEnvironmentVariable("RENDER_SHEET") is not ("1" or "true"))
            return;

        var skins = new (string Name, ICardSkin Skin)[]
        {
            ("classic", new DefaultCardSkin()),
            ("simple",  new SimpleCardSkin()),
        };

        Rank[] ranks = [Rank.Ace, Rank.Two, Rank.Seven, Rank.Ten, Rank.Jack, Rank.Queen, Rank.King];
        Suit[] suits = [Suit.Spades, Suit.Hearts, Suit.Diamonds, Suit.Clubs];

        const int cw = 110, ch = 154, gap = 8;

        foreach (var (name, skin) in skins)
        {
            int w = gap + ranks.Length * (cw + gap);
            int h = gap + suits.Length * (ch + gap);

            using var bitmap = new SKBitmap(w, h);
            using (var canvas = new SKCanvas(bitmap))
            {
                canvas.Clear(new SKColor(0x18, 0x4A, 0x2C));
                for (int s = 0; s < suits.Length; s++)
                    for (int r = 0; r < ranks.Length; r++)
                    {
                        float x = gap + r * (cw + gap);
                        float y = gap + s * (ch + gap);
                        CardRenderer.DrawCardFace(canvas,
                            new SKRect(x, y, x + cw, y + ch),
                            new Card(suits[s], ranks[r], isFaceUp: true), skin);
                    }
            }

            string path = Path.Combine(
                Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath(),
                $"cards-{name}.png");

            using var image = SKImage.FromBitmap(bitmap);
            using var data  = image.Encode(SKEncodedImageFormat.Png, 90);
            using var file  = File.OpenWrite(path);
            data.SaveTo(file);
        }
    }
}
