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
