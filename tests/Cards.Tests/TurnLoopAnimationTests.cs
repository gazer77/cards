using Cards.App;
using Cards.Engine;
using Cards.Services;

namespace Cards.Tests;

/// <summary>
/// Covers the turn loop's contract with the animation layer.
///
/// This is ordering, not appearance, and it is the kind of thing that breaks without
/// failing: a client that never animates still plays a correct game of cards — it just
/// resolves every hand at the speed of the CPU, which is what the browser client did
/// before <see cref="ITableAnimator"/> existed. Nothing about the engine or the rules
/// notices, so only an explicit test does.
/// </summary>
public sealed class TurnLoopAnimationTests
{
    /// <summary>Records the sequence of calls, and what the state looked like at each.</summary>
    private sealed class RecordingAnimator : ITableAnimator
    {
        public List<string> Calls { get; } = [];

        /// <summary>
        /// Card id → zone id, as seen by the first CaptureBeforeMove. Deliberately keyed
        /// off the state itself rather than a hard-coded zone name, so the test says
        /// something about ordering rather than about one game's layout.
        /// </summary>
        public Dictionary<string, string>? FirstCapture { get; private set; }

        public void CaptureBeforeMove(GameState state)
        {
            Calls.Add("capture");

            FirstCapture ??= state.Zones
                .SelectMany(kv => kv.Value.Cards.Select(c => (c.Id, ZoneId: kv.Key)))
                .GroupBy(x => x.Id)
                .ToDictionary(g => g.Key, g => g.First().ZoneId);
        }

        public Task PlayMoveAsync(GameState state)
        {
            Calls.Add("move");
            return Task.CompletedTask;
        }

        public Task PlayDealAsync(GameState state)
        {
            Calls.Add("deal");
            return Task.CompletedTask;
        }
    }

    private sealed class NoSaveStore : ISaveStore
    {
        public bool Exists(string key) => false;
        public void Delete(string key) { }
        public Task WriteAsync(string key, string contents) => Task.CompletedTask;
        public Task<string?> ReadAsync(string key) => Task.FromResult<string?>(null);
    }

    private static GameTableViewModel BuildViewModel(out RecordingAnimator animator)
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));

        animator = new RecordingAnimator();
        return new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()))
        {
            Animator = animator,
        };
    }

    [Fact]
    public async Task Deal_is_choreographed_when_a_game_starts_fresh()
    {
        var vm = BuildViewModel(out var animator);

        Assert.True(await vm.StartAsync("war", 2, resume: false, seed: 7));

        Assert.Contains("deal", animator.Calls);
        // The deal is the opening move; nothing may animate ahead of it.
        Assert.Equal("deal", animator.Calls[0]);
    }

    /// <summary>
    /// Plays a handful of turns. War opens waiting on the player, so nothing animates
    /// until someone acts — without this, assertions about the turn loop pass by
    /// examining an empty list.
    /// </summary>
    private static async Task PlayTurns(GameTableViewModel vm, int turns)
    {
        for (int i = 0; i < turns && !vm.IsGameOver; i++)
            await vm.TapTable();
    }

    [Fact]
    public async Task Every_applied_action_captures_before_it_moves()
    {
        var vm = BuildViewModel(out var animator);
        await vm.StartAsync("war", 2, resume: false, seed: 7);
        await PlayTurns(vm, 5);

        Assert.Contains("move", animator.Calls);

        // Each move must be preceded by its own capture. Reversing the two — or
        // dropping the capture — leaves every card flying from where it landed,
        // which renders as no movement at all.
        var moves = animator.Calls.Where(c => c is "capture" or "move").ToList();
        for (int i = 0; i + 1 < moves.Count; i += 2)
        {
            Assert.Equal("capture", moves[i]);
            Assert.Equal("move",    moves[i + 1]);
        }
    }

    [Fact]
    public async Task Capture_sees_the_state_before_the_action_lands()
    {
        var vm = BuildViewModel(out var animator);
        await vm.StartAsync("war", 2, resume: false, seed: 7);
        await PlayTurns(vm, 5);

        var before = animator.FirstCapture;
        Assert.NotNull(before);
        Assert.NotEmpty(before);

        // War moves cards on every turn, so after several turns the layout must have
        // changed. If capture ran after Apply instead of before it, it would be reading
        // the same state the move produced — and the animation would have no distance
        // to travel.
        var after = vm.State!.Zones
            .SelectMany(kv => kv.Value.Cards.Select(c => (c.Id, ZoneId: kv.Key)))
            .GroupBy(x => x.Id)
            .ToDictionary(g => g.Key, g => g.First().ZoneId);

        Assert.True(
            before.Any(kv => !after.TryGetValue(kv.Key, out var z) || z != kv.Value),
            "The first capture matched the final layout exactly, which means it never " +
            "observed a pre-action state.");
    }

    [Fact]
    public async Task A_game_still_plays_with_no_animator_attached()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()));

        // The default is the null animator, which is what a headless host relies on.
        Assert.True(await vm.StartAsync("war", 2, resume: false, seed: 7));
        Assert.NotNull(vm.State);
    }

    [Fact]
    public void A_null_animator_falls_back_rather_than_throwing()
    {
        var loader = new GameLoader(
            new FileSystemGameAssetSource(FileSystemGameAssetSource.FindRepoRoot()));
        var vm = new GameTableViewModel(loader, new GameSaveService(new NoSaveStore()))
        {
            Animator = null!,
        };

        // Losing the animator must degrade to "cards land instantly", never to a
        // NullReferenceException mid-turn.
        Assert.NotNull(vm.Animator);
    }
}
