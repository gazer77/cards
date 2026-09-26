using Cards.Models;

namespace Cards.Engine.Shared;

/// <summary>
/// Builds one seat's <see cref="TableView"/> from the real game, and turns a view back
/// into a table a client can draw.
///
/// What a seat may see is decided card by card, the way the table decides it: a zone
/// open to everyone shows its face-up cards, a hand shows its owner everything and
/// everyone else backs, a deck shows nobody anything. A card the seat may not see goes
/// out as a back under an alias — never its rank, suit or real uid, since uids are
/// assigned in deck order and a card seen once could be followed by its uid ever after.
/// </summary>
public static class TableProjection
{
    /// <summary>The view <paramref name="viewerId"/> is entitled to, now.</summary>
    public static TableView For(
        GameState state, IGameLogic logic, string viewerId,
        int playerCount, IReadOnlyList<string> enabledRules,
        IReadOnlyList<SeatView> seats, Func<int, int> alias, long version, bool busy,
        IReadOnlyList<(string PlayerId, string Text)> announcements)
    {
        var snap = GameStateSerializer.Snapshot(state, playerCount, enabledRules);

        // Cards: the real ones where this seat may look, aliased backs elsewhere.
        var hidden = new Dictionary<int, int>();   // real uid → alias, for groups and ids
        foreach (var saved in snap.Zones)
        {
            var zone = state.Zones[saved.Id];
            for (int i = 0; i < zone.Cards.Count; i++)
            {
                var card = zone.Cards[i];
                if (Sees(state, zone, card, i, viewerId)) continue;

                int a = alias(card.Uid);
                hidden[card.Uid] = a;
                saved.Cards[i] = new SavedCard { Suit = 0, Rank = (int)Rank.Two, IsFaceUp = false, Uid = a, IsHidden = true };
            }
            saved.Groups = saved.Groups
                .Select(g => g.Select(uid => hidden.GetValueOrDefault(uid, uid)).ToList())
                .ToList();
        }

        // Words: every line in this seat's own reading.
        snap.GameLog  = state.GameLog.Select(l => GameText.Render(state, l, viewerId)).ToList();
        snap.Metadata = state.Metadata.ToDictionary(kv => kv.Key, kv => GameText.Render(state, kv.Value, viewerId));

        // What another seat is picking is theirs until they play it.
        bool gameOver = logic.IsGameOver(state);
        bool acting   = !gameOver && state.Players.Count > 0 && state.CurrentPlayer.Id == viewerId
                     && !state.PlayerAgents.ContainsKey(viewerId);
        if (!acting) snap.Metadata.Remove("selected_card");

        var view = new TableView
        {
            Version      = version,
            GameId       = state.GameId,
            ViewerId     = viewerId,
            PlayerCount  = playerCount,
            EnabledRules = [.. enabledRules],
            State        = snap,
            Seats        = [.. seats],
            Announcements = announcements
                .Select(a => new AnnouncementView { PlayerId = a.PlayerId, Text = GameText.Render(state, a.Text, viewerId) })
                .ToList(),
            Status     = GameText.Render(state, logic.GetStatusText(state), viewerId),
            IsGameOver = gameOver,
            IsBusy     = busy,
        };

        if (gameOver || busy) return view;

        var valid = logic.GetValidActions(state);
        if (!acting)
        {
            // Not this seat's move — but a table gesture ("tap to continue") belongs to
            // everyone sitting there.
            view.Actions = valid.Where(SeatGate.IsTableGesture).ToList();
            return view;
        }

        view.Actions = valid.Select(a => Translate(state, a, hidden)).ToList();

        foreach (var id in logic.GetSelectableCardIds(state).Distinct())
        {
            // A selectable card is named by description; the view names each physical
            // card that answers to it the way this seat sees it.
            foreach (var card in state.Zones.Values.SelectMany(z => z.Cards).Where(c => c.Id == id))
            {
                string viewId = hidden.TryGetValue(card.Uid, out var a) ? $"hidden{a}" : card.Id;
                if (view.SelectableCardIds.Contains(viewId)) continue;
                view.SelectableCardIds.Add(viewId);

                var zones = logic.GetDropZoneIds(state, id);
                if (zones.Count > 0) view.DropZones[viewId] = [.. zones];

                if (logic.GetDefaultCardAction(state, id, card.Uid) is { } d)
                    view.DefaultCardActions[viewId] = Translate(state, d, hidden);
            }
        }

        return view;
    }

    /// <summary>
    /// Whether <paramref name="viewerId"/> may see this card's face. Mirrors how the
    /// table draws a zone, so a view never holds more than its screen would show.
    /// </summary>
    public static bool Sees(GameState state, Zone zone, Card card, int index, string viewerId)
    {
        bool owns = zone.OwnerId == viewerId
                 || state.Teams.Any(t => t.Id == zone.OwnerId && t.PlayerIds.Contains(viewerId));

        // A showdown turns every hand still in over.
        bool revealed = state.Metadata.GetValueOrDefault("showdown_revealed") == "true"
                     && zone.OwnerId is not null
                     && state.Metadata.GetValueOrDefault($"bet_folded:{zone.OwnerId}") != "true";

        return zone.Visibility switch
        {
            "all"           => card.IsFaceUp || revealed,
            "top"           => card.IsFaceUp && index == zone.Cards.Count - 1,
            "owner"         => owns || revealed,
            "mixed"         => owns || card.IsFaceUp || revealed,
            "top_to_dealer" => state.DealerId == viewerId
                               || (card.IsFaceUp && index == zone.Cards.Count - 1),
            _               => false,   // none, count_only
        };
    }

    /// <summary>An action as this seat names its cards.</summary>
    private static GameAction Translate(GameState state, GameAction action, Dictionary<int, int> hidden)
    {
        if (action.CardId is null) return action;

        var card = action.CardUid is int uid
            ? state.Zones.Values.SelectMany(z => z.Cards).FirstOrDefault(c => c.Uid == uid)
            : null;
        if (card is null || !hidden.TryGetValue(card.Uid, out var a)) return action;

        return action with { CardId = $"hidden{a}", CardUid = a };
    }

    /// <summary>
    /// A client's table, rebuilt from a view: the definition's zones and layout, filled
    /// with what the view says is on them, drawn for the view's seat.
    /// </summary>
    public static GameState ToState(TableView view, GameDefinition definition)
    {
        var state = new GameState { GameId = view.GameId, Definition = definition };
        var logic = LogicRegistry.Create(definition);
        GameStateSerializer.Restore(state, logic, view.State, view.PlayerCount, view.EnabledRules);

        foreach (var seat in view.Seats)
            if (state.Players.FirstOrDefault(p => p.Id == seat.Id) is { } player)
                player.Name = seat.Name;

        state.ViewerId = view.ViewerId;
        foreach (var a in view.Announcements)
            state.Announcements.Add((a.PlayerId, a.Text));

        return state;
    }
}
