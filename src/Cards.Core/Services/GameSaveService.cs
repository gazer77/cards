using System.Text.Json;
using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// Persists a running game and restores it on demand, one entry per game ID
/// ("save_war", "save_blackjack", …).
///
/// Storage is supplied by <see cref="ISaveStore"/> so this works unchanged on a
/// phone (files) and in a browser (localStorage). The state-shape logic lives in
/// <see cref="GameStateSerializer"/>, shared with the multiplayer sync path.
/// </summary>
public sealed class GameSaveService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    private readonly ISaveStore _store;

    public GameSaveService(ISaveStore store) => _store = store;

    public bool HasSave(string gameId) => _store.Exists(Key(gameId));

    public void DeleteSave(string gameId) => _store.Delete(Key(gameId));

    public async Task SaveAsync(GameState state, int playerCount, IReadOnlyList<string> enabledRules)
    {
        var dto = GameStateSerializer.Snapshot(state, playerCount, enabledRules);
        await _store.WriteAsync(Key(state.GameId), JsonSerializer.Serialize(dto, _json));
    }

    /// <summary>
    /// Restores saved state into <paramref name="state"/>.
    /// Returns false when no save exists or the stored data is unreadable, in which
    /// case the bad entry is discarded so the player gets a fresh game rather than
    /// a permanent failure.
    /// </summary>
    public async Task<bool> RestoreAsync(
        GameState state,
        IGameLogic logic,
        int playerCount,
        IReadOnlyList<string> enabledRules)
    {
        var json = await _store.ReadAsync(Key(state.GameId));
        if (json is null) return false;

        try
        {
            var dto = JsonSerializer.Deserialize<SavedGameState>(json, _json);
            if (dto is null) return false;

            // A save is only valid for the table it was written at. Restore replaces the
            // zones wholesale, so loading a four-player save into a two-player game
            // leaves hands belonging to players that no longer exist — holding cards
            // nobody can ever ask for, while the deck drains and the game cannot finish.
            //
            // Not deleted: the player may yet start a table that size again, and the
            // save is theirs.
            if (dto.PlayerCount != playerCount) return false;

            GameStateSerializer.Restore(state, logic, dto, playerCount, enabledRules);

            // Same failure by another route — a save whose zones name players this game
            // does not have. That is corruption rather than a mismatch, so it goes.
            if (GameStateSerializer.OrphanedZones(state).Count > 0)
            {
                DeleteSave(state.GameId);
                return false;
            }

            return true;
        }
        catch
        {
            DeleteSave(state.GameId);
            return false;
        }
    }

    private static string Key(string gameId) => $"save_{gameId}";
}
