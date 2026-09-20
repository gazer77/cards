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
        public event Action? Tick;
        public void RequestFrames() { }
        public void StopFrames() { }
    }

    [Fact]
    public void Write_table_sheet()
    {
        if (Environment.GetEnvironmentVariable("TABLE_SHEET") is not ("1" or "true"))
            return;

        string dir = Environment.GetEnvironmentVariable("SHEET_DIR") ?? Path.GetTempPath();
        var loader = new GameLoader(new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        foreach (var def in loader.LoadAllAsync().GetAwaiter().GetResult())
        {
            foreach (int seats in new SortedSet<int> { def.MinPlayers, def.MaxPlayers })
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

                var renderer = new CardTableRenderer(new StubDriver()) { GameState = state, RevealAllCards = true };
                const int w = 1400, h = 900;
                using var bitmap = new SKBitmap(w, h);
                using var canvas = new SKCanvas(bitmap);

                // Assigning a state queues deal animations; a snapshot mid-slide shows
                // every hand 70 px off. Let them run out, as a real frame loop would.
                renderer.Paint(canvas, new SKImageInfo(w, h));
                Thread.Sleep(400);
                canvas.Clear();
                renderer.Paint(canvas, new SKImageInfo(w, h));

                using var image = SKImage.FromBitmap(bitmap);
                using var data  = image.Encode(SKEncodedImageFormat.Png, 85);
                using var file  = File.OpenWrite(Path.Combine(dir, $"table-{def.Id}-{seats}p.png"));
                data.SaveTo(file);
            }
        }
    }
}
