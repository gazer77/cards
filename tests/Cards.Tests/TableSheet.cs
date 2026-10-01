using SkiaSharp;
using Cards.Engine;
using Cards.Rendering;

namespace Cards.Tests;

/// <summary>
/// Renders every shipped game's table, a few turns in, for eyeballing. Opt in with
/// TABLE_SHEET=1; images land in SHEET_DIR. The layout test proves nothing overlaps
/// and nothing is off the table; only a picture says whether it looks like the game.
/// </summary>
[Collection(CardCacheCollection.Name)]
public sealed class TableSheet
{
    private sealed class StubDriver : IAnimationDriver
    {
        public event Action? Tick { add { } remove { } }   // never ticks: these tests paint by hand
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    /// <summary>
    /// A desktop table, and a phone held upright: 390 by about 790 CSS pixels of table,
    /// drawn at the web client's 1.5 pixel cap, with its labels doubled as a phone has them.
    /// The footer floats over the bottom 4.4rem; the table is laid out above it.
    /// </summary>
    public static readonly (int W, int H, float BottomInset, float LabelScale, string Suffix)[] Screens =
    [
        (1400, 900,  70f, 1f, ""),
        (585,  1185, 106f, 2f, "-phone"),
    ];

    public static void Paint(GameState state, (int W, int H, float BottomInset, float LabelScale, string Suffix) screen, string path)
    {
        var renderer = new CardTableRenderer(new StubDriver())
        {
            GameState = state, RevealAllCards = true, BottomInset = screen.BottomInset, LabelScale = screen.LabelScale,
            // A phone opens the scores as a sheet of its own instead of drawing them.
            ShowScoreCard = screen.LabelScale == 1f,
        };
        using var bitmap = new SKBitmap(screen.W, screen.H);
        using var canvas = new SKCanvas(bitmap);

        // Assigning a state queues deal animations; a snapshot mid-slide shows every hand
        // 70 px off. Let them run out, as a real frame loop would.
        renderer.Paint(canvas, new SKImageInfo(screen.W, screen.H));
        Thread.Sleep(400);
        canvas.Clear();
        renderer.Paint(canvas, new SKImageInfo(screen.W, screen.H));

        using var image = SKImage.FromBitmap(bitmap);
        using var data  = image.Encode(SKEncodedImageFormat.Png, 85);
        using var file  = File.Create(path);
        data.SaveTo(file);
    }

    [Fact]
    public void Write_table_sheet()
    {
        if (Environment.GetEnvironmentVariable("TABLE_SHEET") is not ("1" or "true"))
            return;

        string dir = Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath();
        var loader = new GameLoader(new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        foreach (var def in TestGames.LoadAll(loader))
        {
            foreach (int seats in new SortedSet<int> { def.MinPlayers, Math.Clamp(4, def.MinPlayers, def.MaxPlayers), def.MaxPlayers })
            {
                var state = new GameState { GameId = def.Id, Definition = def, Rng = new SeededRandomSource(7) };
                var logic = LogicRegistry.Create(def);
                logic.Initialize(state, seats, []);

                // Every seat plays itself, so the table fills: tricks, melds, pots.
                foreach (var p in state.Players)
                    state.PlayerAgents[p.Id] = new SmartDefaultAiAgent(p.Id, state.Rng);
                for (int step = 0; step < 12 && !logic.IsGameOver(state); step++)
                {
                    if (logic.GetAutoAdvanceDelay(state) is null) break;
                    var valid = logic.GetValidActions(state);
                    if (valid.Count == 0 && logic.GetSelectableCardIds(state).Count == 0) break;
                    logic.Apply(state, logic.GetAutoAction(state));
                }

                foreach (var screen in Screens)
                    Paint(state, screen, Path.Combine(dir, $"table-{def.Id}-{seats}p{screen.Suffix}.png"));
            }
        }
    }
}
