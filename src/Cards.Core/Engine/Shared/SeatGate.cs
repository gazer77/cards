namespace Cards.Engine.Shared;

/// <summary>
/// Whether a seat may take an action, now. The one question a shared table has to ask
/// before it applies anything a player sends.
///
/// A single-player table never asked it: the only person there was the only one who
/// could press anything, and the screen offered only what the rules allowed. At a shared
/// table the screen is someone else's program. So the answer is the same set the seat's
/// own view was offered — its buttons, the cards it may pick and where they may go — and
/// never more: an action out of turn, a card not on offer, or a zone the card cannot go
/// to is refused however it arrived.
/// </summary>
public static class SeatGate
{
    /// <summary>
    /// A gesture that belongs to the table rather than to whoever is to act — "tap to
    /// continue" after a hand is settled, "tap to flip" at War. Any seat may make it,
    /// and only when the rules are offering it.
    /// </summary>
    public static bool IsTableGesture(GameAction action)
        => action.Type is "tap" or "ready";

    public static bool Allows(GameState state, IGameLogic logic, string seatId, GameAction action, out string? reason)
    {
        reason = null;

        if (logic.IsGameOver(state))                  { reason = "The game is over."; return false; }
        if (state.PlayerAgents.ContainsKey(seatId))   { reason = "That seat is a computer player."; return false; }
        if (state.Players.All(p => p.Id != seatId))   { reason = "That seat is not at this table."; return false; }

        var valid = logic.GetValidActions(state);

        if (action.Type is "tap" or "ready")
        {
            if (valid.Any(a => a.Type == action.Type)) return true;
            reason = "Nothing to continue.";
            return false;
        }

        if (state.CurrentPlayer.Id != seatId) { reason = "It is not your turn."; return false; }

        if (valid.Any(a => Matches(a, action))) return true;

        // Card gestures: picking, playing and turning are offered by card rather than
        // listed as actions.
        if (action.CardId is { } cardId && action.Type is "select_card" or "play_card" or "flip_card")
        {
            if (!logic.GetSelectableCardIds(state).Contains(cardId))
            {
                reason = "That card is not in play.";
                return false;
            }
            if (action.Type == "play_card" && action.ZoneId is { } zoneId
                && !logic.GetDropZoneIds(state, cardId).Contains(zoneId))
            {
                reason = "That card cannot go there.";
                return false;
            }
            return true;
        }

        // Letting go of a selection never changes the game.
        if (action.Type is "clear_selection" or "deselect") return true;

        // A double tap's shortcut is one of the phase's own moves.
        if (action.CardId is { } tapped
            && logic.GetDefaultCardAction(state, tapped, action.CardUid) is { } shortcut
            && Matches(shortcut, action))
            return true;

        reason = "That move is not available.";
        return false;
    }

    private static bool Matches(GameAction offered, GameAction sent)
        => offered.Type == sent.Type
        && (offered.CardId is null || offered.CardId == sent.CardId)
        && (offered.ZoneId is null || offered.ZoneId == sent.ZoneId);
}
