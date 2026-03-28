using System.Text.Json;
using Cards.Engine;

namespace Cards.Services;

/// <summary>
/// Persists a running game to AppDataDirectory as JSON and restores it on demand.
/// One file per game ID: "save_war.json", "save_blackjack.json", etc.
/// </summary>
public sealed class GameSaveService
{
    private static readonly JsonSerializerOptions _json = new() { WriteIndented = false };

    // ── Public API ────────────────────────────────────────────────────────────

    public bool HasSave(string gameId)
        => File.Exists(SavePath(gameId));

    public void DeleteSave(string gameId)
    {
        var path = SavePath(gameId);
        if (File.Exists(path)) File.Delete(path);
    }

    public async Task SaveAsync(GameState state, int playerCount, IReadOnlyList<string> enabledRules)
    {
        var dto = new SavedGameState
        {
            GameId       = state.GameId,
            PlayerCount  = playerCount,
            EnabledRules = enabledRules.ToList(),
            PhaseId      = state.CurrentPhaseId,
            PlayerIndex  = state.CurrentPlayerIndex,
            RoundNumber  = state.RoundNumber,
            Scores       = new Dictionary<string, int>(state.Scores),
            Metadata     = new Dictionary<string, string>(state.Metadata),
            GameLog      = [.. state.GameLog],
            Zones        = state.Zones.Values.Select(z => new SavedZone
            {
                Id         = z.Id,
                Type       = z.Type,
                OwnerId    = z.OwnerId,
                Visibility = z.Visibility,
                Cards      = z.Cards.Select(c => new SavedCard
                {
                    Suit     = (int)c.Suit,
                    Rank     = (int)c.Rank,
                    IsFaceUp = c.IsFaceUp,
                    IsWild   = c.IsWild,
                }).ToList(),
            }).ToList(),
        };

        var json = JsonSerializer.Serialize(dto, _json);
        await File.WriteAllTextAsync(SavePath(state.GameId), json);
    }

    /// <summary>
    /// Restores saved state into <paramref name="state"/>.
    /// Calls <paramref name="logic"/>.Initialize first so logic-private fields are
    /// properly set, then overlays all runtime state from the save file.
    /// Returns false if no save exists or if the file is corrupt.
    /// </summary>
    public async Task<bool> RestoreAsync(
        GameState state,
        IGameLogic logic,
        int playerCount,
        IReadOnlyList<string> enabledRules)
    {
        var path = SavePath(state.GameId);
        if (!File.Exists(path)) return false;

        try
        {
            var json = await File.ReadAllTextAsync(path);
            var dto  = JsonSerializer.Deserialize<SavedGameState>(json, _json);
            if (dto is null) return false;

            // Re-run Initialize to set up logic-private state (e.g. house-rule flags).
            // This also populates state.Players and initial zones, which we'll replace.
            logic.Initialize(state, playerCount, enabledRules);

            // Overlay runtime state from the save.
            state.Zones.Clear();
            foreach (var sz in dto.Zones)
            {
                var zone = new Zone(sz.Id, sz.Type, sz.OwnerId, sz.Visibility);
                foreach (var sc in sz.Cards)
                    zone.Add(new Card((Suit)sc.Suit, (Rank)sc.Rank, sc.IsFaceUp) { IsWild = sc.IsWild });
                state.Zones[sz.Id] = zone;
            }

            state.CurrentPhaseId     = dto.PhaseId;
            state.CurrentPlayerIndex = dto.PlayerIndex;
            state.RoundNumber        = dto.RoundNumber;

            state.Scores.Clear();
            foreach (var (k, v) in dto.Scores)   state.Scores[k]   = v;

            state.Metadata.Clear();
            foreach (var (k, v) in dto.Metadata) state.Metadata[k] = v;

            state.GameLog.Clear();
            state.GameLog.AddRange(dto.GameLog);

            return true;
        }
        catch
        {
            // Corrupt or incompatible save — fall back to a fresh game.
            DeleteSave(state.GameId);
            return false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a SavedGameState DTO from a live GameState without writing to disk.
    /// Used by the multiplayer server to send state-sync messages to joining clients.
    /// </summary>
    public static SavedGameState Snapshot(GameState state, int playerCount, IReadOnlyList<string> enabledRules)
        => new()
        {
            GameId       = state.GameId,
            PlayerCount  = playerCount,
            EnabledRules = enabledRules.ToList(),
            PhaseId      = state.CurrentPhaseId,
            PlayerIndex  = state.CurrentPlayerIndex,
            RoundNumber  = state.RoundNumber,
            Scores       = new Dictionary<string, int>(state.Scores),
            Metadata     = new Dictionary<string, string>(state.Metadata),
            GameLog      = [.. state.GameLog],
            Zones        = state.Zones.Values.Select(z => new SavedZone
            {
                Id         = z.Id,
                Type       = z.Type,
                OwnerId    = z.OwnerId,
                Visibility = z.Visibility,
                Cards      = z.Cards.Select(c => new SavedCard
                {
                    Suit     = (int)c.Suit,
                    Rank     = (int)c.Rank,
                    IsFaceUp = c.IsFaceUp,
                    IsWild   = c.IsWild,
                }).ToList(),
            }).ToList(),
        };

    private static string SavePath(string gameId)
        => Path.Combine(FileSystem.AppDataDirectory, $"save_{gameId}.json");
}
