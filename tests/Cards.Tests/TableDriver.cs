using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Plays a table the way the app does.
///
/// Written after four attempts at ad-hoc drivers, each of which measured itself rather
/// than the game: substituting a heuristic for GetAutoAction halved the games that
/// finished; tapping the first selectable card toggled one card forever, because
/// selection is a toggle; and preferring actions over card taps left every trick-taking
/// game applying a no-op "tap" on the human's turn while three playable cards sat there.
/// All three looked exactly like a livelocked game.
///
/// The rule the app uses is not "apply any available action". It is:
///
///   GetAutoAdvanceDelay non-null → nobody needs to decide; apply the engine's own
///                                  choice, which is what an AI seat does.
///   null                         → a person decides. The table then offers card taps,
///                                  and shows buttons only when there is a real choice
///                                  between them — a single action is triggered by
///                                  tapping the table, not by a button.
///
/// Encoded once here so a test cannot quietly diverge from the client it stands in for.
/// </summary>
public static class TableDriver
{
    public enum StepResult { Moved, Finished, Stuck }

    /// <summary>
    /// Advances one step. <paramref name="tapCursor"/> rotates through a hand across
    /// calls; several handlers toggle selection, so tapping the same card repeatedly
    /// selects and deselects it forever.
    /// </summary>
    public static StepResult Step(GameState state, IGameLogic logic, ref int tapCursor)
    {
        if (logic.IsGameOver(state)) return StepResult.Finished;

        var actions = logic.GetValidActions(state);

        // Nobody needs to decide: this is an automatic step, so take the engine's own
        // choice. When it offers no action the seat still has to act by tapping cards —
        // an AI passing three cards at Hearts does exactly that — so fall through
        // rather than calling the game stuck.
        if (logic.GetAutoAdvanceDelay(state) is not null && actions.Count > 0)
        {
            logic.Apply(state, logic.GetAutoAction(state));
            return StepResult.Moved;
        }

        // Playing a selected card comes first: leaving it selected and doing something
        // else is how a hand ends up going nowhere.
        var selected = SelectedCard(state);
        if (selected is not null)
        {
            var zones = logic.GetDropZoneIds(state, selected);
            if (zones.Count > 0)
            {
                logic.Apply(state, new GameAction("play_card", CardId: selected, ZoneId: zones[0]));
                return StepResult.Moved;
            }
        }

        // Cards before buttons. Most buttons act on a selection — "Meld", "Ask for
        // Queens" — and pressing one with nothing selected does nothing at all. Hand
        // and Foot sat pressing Meld forever with fifteen cards it had never picked up.
        //
        // Selecting first is safe even where a button needs no selection: a tap makes
        // the selection non-empty, so the next step presses the button anyway.
        var selectable = logic.GetSelectableCardIds(state);
        if (selected is null && selectable.Count > 0)
        {
            logic.Apply(state, new GameAction(
                "select_card", CardId: selectable[tapCursor++ % selectable.Count]));
            return StepResult.Moved;
        }

        // Buttons appear only for a real choice; a lone action is the canvas-tap path.
        bool buttonsShown = actions.Count > 1
                         || (actions.Count == 1 && actions[0].Type == "ready");

        if (buttonsShown)
        {
            var choice = actions.FirstOrDefault(a => a.Type is not ("deselect" or "cancel"))
                         ?? actions[0];
            logic.Apply(state, choice);
            return StepResult.Moved;
        }

        if (selectable.Count > 0)
        {
            logic.Apply(state, new GameAction(
                "select_card", CardId: selectable[tapCursor++ % selectable.Count]));
            return StepResult.Moved;
        }

        // Nothing to tap; the lone action is all that is on offer.
        if (actions.Count == 1)
        {
            logic.Apply(state, actions[0]);
            return StepResult.Moved;
        }

        return StepResult.Stuck;
    }

    /// <summary>
    /// The single selected card, or null. A multi-card selection is comma separated and
    /// is submitted with a button rather than by dropping it on a zone.
    /// </summary>
    public static string? SelectedCard(GameState state)
    {
        var value = state.Metadata.GetValueOrDefault("selected_card", "");
        return string.IsNullOrEmpty(value) || value.Contains(',') ? null : value;
    }
}
