namespace Cards.Engine;

/// <summary>
/// Default setup implementation.  Reads <see cref="Cards.Models.GameDefinition"/>
/// and populates a <see cref="GameState"/> with:
///
///   • Players — IDs are "player0", "player1", … N-1.
///     Display names come from <c>players.names</c> in the definition;
///     defaults to "Player 1", "Player 2", … when absent.
///
///   • Zones — every entry in <c>definition.Zones</c> is created.
///     Zones with <c>"owner": "each_player"</c> are expanded into one zone
///     per player, named <c>"{zoneId}:{playerId}"</c>.
///     All other zones are created with their literal ID and owner.
///
/// Logic classes that need zones not declared in the JSON (temporary or
/// logic-private zones) should call <see cref="Instance"/> first, then
/// add the extra zones to <c>state.Zones</c> themselves.
/// </summary>
public sealed class SetupEngine : ISetupStrategy
{
    public static readonly SetupEngine Instance = new();

    // ── ISetupStrategy ───────────────────────────────────────────────────────

    public void Setup(GameState state, int playerCount, IReadOnlyList<string> enabledRules)
    {
        CreatePlayers(state, playerCount);
        ExpandZones(state);
        AssignTeams(state, playerCount);
    }

    // ── Helpers (internal so GameTablePage.BuildFallbackState can reuse) ─────

    internal static void CreatePlayers(GameState state, int playerCount)
    {
        var names = state.Definition.Players?.Names;
        for (int i = 0; i < playerCount; i++)
        {
            string id   = $"player{i}";
            string name = names is not null && i < names.Count ? names[i] : $"Player {i + 1}";
            state.Players.Add(new Player(id, name));
        }
    }

    internal static void AssignTeams(GameState state, int playerCount)
    {
        var config = state.Definition.TeamsConfig;
        if (config is null) return;

        // Respect "only_when_players" restriction.
        if (config.OnlyWhenPlayers is { Count: > 0 } restriction
            && !restriction.Contains(playerCount))
            return;

        // Build team objects.
        for (int t = 0; t < config.Count; t++)
            state.Teams.Add(new Team(t));

        // Assign players to teams.
        for (int i = 0; i < state.Players.Count; i++)
        {
            int teamIdx = config.Arrangement.Equals("sequential", StringComparison.OrdinalIgnoreCase)
                ? i / config.Size          // sequential: 0,1 → team0 | 2,3 → team1
                : i % config.Count;        // alternating: 0,2 → team0 | 1,3 → team1

            if (teamIdx >= state.Teams.Count) continue;

            var team = state.Teams[teamIdx];
            team.PlayerIds.Add(state.Players[i].Id);
            state.Players[i].TeamIndex = teamIdx;
        }
    }

    internal static void ExpandZones(GameState state)
    {
        foreach (var zoneDef in state.Definition.Zones)
        {
            if (zoneDef.Owner == "each_player")
            {
                foreach (var p in state.Players)
                {
                    string id = $"{zoneDef.Id}:{p.Id}";
                    state.Zones[id] = new Zone(id, zoneDef.Type, p.Id, zoneDef.Visibility);
                }
            }
            else
            {
                state.Zones[zoneDef.Id] =
                    new Zone(zoneDef.Id, zoneDef.Type, zoneDef.Owner, zoneDef.Visibility);
            }
        }
    }
}
