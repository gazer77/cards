using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Every status line says who it is about. A line that never said falls back, as a
/// bubble, to whoever had just acted — which is how War's "Opponent wins" once appeared
/// beside the player, and a round's summary beside whoever made the last move. So play
/// each game a while and hold every line it writes to having recorded its subject: a
/// seat, the reader, or nobody.
/// </summary>
public sealed class StatusSubjectTests
{
    private static GameLoader NewLoader() => new(new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

    public static TheoryData<string, int> Tables
    {
        get
        {
            var data = new TheoryData<string, int>();
            foreach (var def in TestGames.LoadAll(NewLoader()))
                if (def.Id != "free-play")
                    foreach (int n in new[] { def.MinPlayers, def.MaxPlayers }.Distinct())
                        data.Add(def.Id, n);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_line_a_game_writes_says_who_it_is_about(string gameId, int players)
    {
        var definition = TestGames.Load(NewLoader(), gameId)!;
        var unsaid = new SortedSet<string>(StringComparer.Ordinal);

        foreach (ulong seed in new ulong[] { 1, 7, 42 })
        {
            var state = new GameState
            {
                GameId = definition.Id, Definition = definition,
                Rng = new SeededRandomSource(seed), Seed = seed,
            };
            var logic = LogicRegistry.Create(definition);
            logic.Initialize(state, players, []);

            int tap = 0;
            for (int step = 0; step < 1500 && !logic.IsGameOver(state); step++)
            {
                Check(state, logic, unsaid);
                if (TableDriver.Step(state, logic, ref tap) != TableDriver.StepResult.Moved) break;
            }
            Check(state, logic, unsaid);
        }

        Assert.True(unsaid.Count == 0,
            $"{gameId} wrote lines that never said who they are about:\n  " + string.Join("\n  ", unsaid));
    }

    private static void Check(GameState state, IGameLogic logic, SortedSet<string> unsaid)
    {
        string text = logic.GetStatusText(state);
        if (text.Length > 0 && logic.GetStatusSubject(state, state.Viewer) is null)
            unsaid.Add(text.Replace("\n", " ⏎ "));
    }
}
