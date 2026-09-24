using System.Text.Json;
using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Locks in current engine behaviour for every shipped game.
///
/// These are characterization tests, not correctness tests: the committed hashes
/// describe what the engine does TODAY. Their job is to fail loudly if the large
/// refactors ahead (extracting Cards.Core, adding Card.Uid, splitting GameTableView)
/// change behaviour by accident.
///
/// To re-record after an INTENTIONAL behaviour change:
///     RECORD_GOLDEN=1 dotnet test --filter GoldenMaster
/// then read the diff on golden-master.json carefully before committing it.
/// </summary>
public sealed class GoldenMasterTests
{
    private static readonly string RepoRoot   = FileSystemGameAssetSource.FindRepoRoot();
    private static readonly string GoldenPath =
        Path.Combine(RepoRoot, "tests", "Cards.Tests", "golden-master.json");

    private static readonly ulong[] Seeds = [1UL, 20260827UL, 0xC0FFEEUL];

    private static GameLoader NewLoader() => new(new FileSystemGameAssetSource(RepoRoot));

    /// <summary>
    /// Every (game, shape, playerCount, seed) combination, derived from the shipped
    /// definitions. A named shape is a game in its own right — Stud and Hold'em are one
    /// file and two games — so each is recorded separately, and merging three poker
    /// files into one does not quietly drop two games' worth of cover.
    /// </summary>
    public static TheoryData<string, string?, int, ulong> Cases
    {
        get
        {
            var data = new TheoryData<string, string?, int, ulong>();
            foreach (var def in NewLoader().LoadAllAsync().GetAwaiter().GetResult())
            {
                // Min and max seats exercise the seating/teams branches that differ by count.
                var counts = new SortedSet<int> { def.MinPlayers, def.MaxPlayers };
                foreach (var count in counts)
                {
                    var shapes = Cards.Engine.GameConfiguration.Offered(def, count)
                        .Select(c => c.Name)
                        .DefaultIfEmpty(null)
                        .ToList();

                    foreach (var shape in shapes)
                        foreach (var seed in Seeds)
                            data.Add(def.Id, shape, count, seed);
                }
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Run_matches_recorded_behaviour(string gameId, string? shape, int playerCount, ulong seed)
    {
        var result = await EngineRunner.RunAsync(NewLoader(), gameId, playerCount, seed, shape);
        string key = Key(gameId, shape, playerCount, seed);

        var golden = LoadGolden();

        if (Recording)
        {
            lock (RecordLock)
            {
                var current = LoadGolden();
                current[key] = Encode(result);
                Save(current);
            }
            return;
        }

        Assert.True(golden.ContainsKey(key),
            $"No recorded behaviour for '{key}'. This case is new — record it with " +
            $"RECORD_GOLDEN=1 and review the addition before committing.");

        Assert.Equal(golden[key], Encode(result));
    }

    /// <summary>
    /// A seeded run must be reproducible, or the golden masters above mean nothing.
    /// This is the test that proves the RNG plumbing actually took.
    /// </summary>
    [Theory]
    [InlineData("hearts", 4)]
    [InlineData("poker", 4)]
    [InlineData("hand-and-foot", 4)]
    public async Task Same_seed_produces_identical_run(string gameId, int playerCount)
    {
        var a = await EngineRunner.RunAsync(NewLoader(), gameId, playerCount, 4242UL);
        var b = await EngineRunner.RunAsync(NewLoader(), gameId, playerCount, 4242UL);

        Assert.Equal(a.Digest, b.Digest);
        Assert.Equal(a.Steps, b.Steps);
    }

    /// <summary>
    /// Different seeds must diverge, otherwise the seed is being ignored somewhere
    /// and "reproducible" would be hiding a hard-coded shuffle.
    /// </summary>
    [Fact]
    public async Task Different_seeds_produce_different_runs()
    {
        var a = await EngineRunner.RunAsync(NewLoader(), "hearts", 4, 1UL);
        var b = await EngineRunner.RunAsync(NewLoader(), "hearts", 4, 2UL);

        Assert.NotEqual(a.Digest, b.Digest);
    }

    // ── Recording plumbing ────────────────────────────────────────────────────

    private static readonly object RecordLock = new();

    private static bool Recording =>
        Environment.GetEnvironmentVariable("RECORD_GOLDEN") is "1" or "true";

    private static string Key(string gameId, string? shape, int playerCount, ulong seed)
        => $"{gameId}{(shape is null ? "" : "[" + shape + "]")}/{playerCount}p/seed{seed}";

    private static string Encode(EngineRunner.Result r)
        => $"{r.Digest} steps={r.Steps} over={r.ReachedGameOver} phase={r.FinalPhase}";

    private static Dictionary<string, string> LoadGolden()
    {
        if (!File.Exists(GoldenPath)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(GoldenPath))
               ?? [];
    }

    private static void Save(Dictionary<string, string> golden)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(GoldenPath)!);
        var ordered = golden.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                            .ToDictionary(kv => kv.Key, kv => kv.Value);
        File.WriteAllText(GoldenPath,
            JsonSerializer.Serialize(ordered, new JsonSerializerOptions { WriteIndented = true }));
    }
}
