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

            GameStateSerializer.Restore(state, logic, dto, playerCount, enabledRules);
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
