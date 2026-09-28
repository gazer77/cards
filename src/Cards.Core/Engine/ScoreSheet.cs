using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// The score card's contents: a row per player (or per team), each with its running
/// total and what it scored round by round. One reading of the scores for everything
/// that shows them — the panel the table draws and the score sheet a phone opens — so
/// the two can never disagree about who has what.
/// </summary>
public sealed record ScoreSheet(
    string Label,
    string RoundLabel,
    IReadOnlyList<int> Rounds,
    IReadOnlyList<ScoreSheetRow> Rows)
{
    /// <summary>
    /// The sheet for this table, laid out as its definition's <c>score_card</c> says — or,
    /// for a game that declares none, a plain one: by team where there are teams, by
    /// player otherwise. Null when there is nothing to show: no score has been kept.
    /// </summary>
    public static ScoreSheet? For(GameState state, int? maxRounds = null)
    {
        var card = state.Definition.ScoreCard;
        bool declared = card is not null;
        if (!declared && state.Scores.Count == 0 && state.ScoreHistory.Count == 0) return null;

        bool byTeam = card?.By == "team" || (!declared && state.Teams.Count > 0);
        int  keep   = Math.Max(1, maxRounds ?? card?.MaxRounds ?? int.MaxValue);
        var  rounds = state.ScoreHistory.TakeLast(keep).ToList();

        string viewer = state.Viewer;
        var rows = byTeam
            ? state.Teams.Select(t => Row(t.Id, t.Name, state.GetTeamScore(t.Id), t.PlayerIds.Contains(viewer))).ToList()
            // Role seats hold no score — the house is not a player — so they get no row.
            : state.Players.Where(p => p.Role is null)
                           .Select(p => Row(p.Id, p.Name, state.GetScore(p.Id), p.Id == viewer)).ToList();

        if (rows.Count == 0) return null;

        return new ScoreSheet(
            card?.Label ?? "Scores",
            card?.RoundLabel ?? "R",
            rounds.Select(r => r.Round).ToList(),
            rows);

        ScoreSheetRow Row(string id, string name, int total, bool isMe)
            => new(id, name, total, isMe,
                   rounds.Select(r => r.Scores.TryGetValue(id, out var v) ? (int?)v : null).ToList());
    }
}

/// <summary>One side's line on the score sheet: its total, and each shown round's score (null where it has none).</summary>
public sealed record ScoreSheetRow(string Id, string Name, int Total, bool IsMe, IReadOnlyList<int?> ByRound);
