using System.Text.Json;
using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// Persists running games and lists them for resuming.
///
/// Every saved game gets its own slot rather than one slot per game, so a player can
/// keep several going at once — a four-player Hearts and a two-player Hearts no longer
/// compete for the same entry, which is what previously let a save written at one table
/// size be loaded into another.
///
/// An index of slot descriptions is kept alongside the saves so the resume list can be
/// shown without deserialising every game. The index is cached in memory because
/// callers ask about saves while building a screen, where an await per game is not
/// worth the correctness it would buy.
///
/// Storage comes from <see cref="ISaveStore"/>, so this works unchanged on a phone
/// (files) and in a browser (localStorage). State shape lives in
/// <see cref="GameStateSerializer"/>, shared with the multiplayer sync path.
/// </summary>
public sealed class GameSaveService
{
    private const string IndexKey = "save_index";

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private readonly ISaveStore _store;

    private List<SaveSlot>? _index;

    public GameSaveService(ISaveStore store) => _store = store;

    // ── Index ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads the slot index. Safe to call repeatedly; only the first read hits storage.
    /// Call once at startup so the synchronous accessors have something to answer with.
    /// </summary>
    public async Task EnsureLoadedAsync()
    {
        if (_index is not null) return;

        try
        {
            var json = await _store.ReadAsync(IndexKey);
            _index = json is null
                ? []
                : JsonSerializer.Deserialize<List<SaveSlot>>(json, _json) ?? [];
        }
        catch
        {
            // A damaged index must not make the app unstartable. The saves themselves
            // are still on disk; the worst case is that they stop being listed.
            _index = [];
        }
    }

    /// <summary>Saved games, newest first.</summary>
    public IReadOnlyList<SaveSlot> Saves =>
        (_index ?? []).OrderByDescending(s => s.SavedAt).ToList();

    /// <summary>Saved games for one game id, newest first.</summary>
    public IReadOnlyList<SaveSlot> SavesFor(string gameId) =>
        Saves.Where(s => s.GameId == gameId).ToList();

    public SaveSlot? FindSlot(string slotId) => (_index ?? []).FirstOrDefault(s => s.Id == slotId);

    public bool HasSave(string gameId) => SavesFor(gameId).Count > 0;

    // ── Saving ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes a game to <paramref name="slotId"/>, or to a new slot when none is given.
    /// Returns the slot id, which the caller should keep so later saves update the same
    /// entry instead of filling the list with one row per turn.
    /// </summary>
    public async Task<string> SaveAsync(
        GameState state,
        int playerCount,
        IReadOnlyList<string> enabledRules,
        string? slotId = null)
    {
        await EnsureLoadedAsync();

        slotId ??= Guid.NewGuid().ToString("N")[..12];

        var dto = GameStateSerializer.Snapshot(state, playerCount, enabledRules);
        await _store.WriteAsync(SaveKey(slotId), JsonSerializer.Serialize(dto, _json));

        var slot = FindSlot(slotId);
        if (slot is null)
        {
            slot = new SaveSlot { Id = slotId };
            _index!.Add(slot);
        }

        slot.GameId       = state.GameId;
        slot.GameName     = state.Definition.Name;
        slot.PlayerCount  = playerCount;
        slot.EnabledRules = enabledRules.ToList();
        slot.SavedAt      = DateTimeOffset.Now;
        slot.Summary      = Describe(state);

        await WriteIndexAsync();
        return slotId;
    }

    /// <summary>A one-line description of where the game had got to.</summary>
    private static string Describe(GameState state)
    {
        var parts = new List<string>();

        if (state.RoundNumber > 0) parts.Add($"Round {state.RoundNumber}");

        if (state.Scores.Count > 0)
        {
            var scores = state.Players
                .Where(p => state.Scores.ContainsKey(p.Id))
                .Select(p => $"{p.Name} {state.GetScore(p.Id)}");
            parts.Add(string.Join(", ", scores));
        }

        int inHands = state.Zones.Values.Where(z => z.Type == "hand").Sum(z => z.Count);
        if (inHands > 0) parts.Add($"{inHands} cards in play");

        return parts.Count > 0 ? string.Join(" — ", parts) : "In progress";
    }

    // ── Restoring ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Restores a slot into <paramref name="state"/>.
    ///
    /// The player count and house rules come from the save itself, not from the caller:
    /// a game restored at a different size keeps hands for seats that no longer exist,
    /// whose cards then count toward the deck while being unreachable. Returns false if
    /// the slot is missing or unreadable, in which case the caller should start a fresh
    /// game rather than fail.
    /// </summary>
    public async Task<bool> RestoreAsync(GameState state, IGameLogic logic, string slotId)
    {
        await EnsureLoadedAsync();

        var slot = FindSlot(slotId);
        if (slot is null) return false;

        var json = await _store.ReadAsync(SaveKey(slotId));
        if (json is null)
        {
            // Indexed but not present — the index is describing something that is gone.
            await DeleteAsync(slotId);
            return false;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SavedGameState>(json, _json);
            if (dto is null) return false;

            GameStateSerializer.Restore(state, logic, dto, dto.PlayerCount, dto.EnabledRules);

            // A save whose zones name players this game does not have is corrupt rather
            // than merely mismatched: its cards can never be reached.
            if (GameStateSerializer.OrphanedZones(state).Count > 0)
            {
                await DeleteAsync(slotId);
                return false;
            }

            return true;
        }
        catch
        {
            await DeleteAsync(slotId);
            return false;
        }
    }

    // ── Deleting ──────────────────────────────────────────────────────────────

    public async Task DeleteAsync(string slotId)
    {
        await EnsureLoadedAsync();

        _store.Delete(SaveKey(slotId));
        _index!.RemoveAll(s => s.Id == slotId);
        await WriteIndexAsync();
    }

    /// <summary>Removes every save for a game — used when starting it over.</summary>
    public async Task DeleteAllForAsync(string gameId)
    {
        await EnsureLoadedAsync();

        foreach (var slot in SavesFor(gameId))
            _store.Delete(SaveKey(slot.Id));

        _index!.RemoveAll(s => s.GameId == gameId);
        await WriteIndexAsync();
    }

    private async Task WriteIndexAsync()
        => await _store.WriteAsync(IndexKey, JsonSerializer.Serialize(_index, _json));

    private static string SaveKey(string slotId) => $"save_{slotId}";
}
