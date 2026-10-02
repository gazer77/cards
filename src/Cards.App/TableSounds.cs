using Cards.Engine;

namespace Cards.App;

/// <summary>The sounds a table makes.</summary>
public enum TableCue { Shuffle, Deal, Play, Draw, Flip, Gather, Score, YourTurn, Win, Lose }

/// <summary>Plays a table's sounds. Each client has its own way to make a noise; a test has none.</summary>
public interface ITableSounds
{
    void Play(TableCue cue);
}

public sealed class NullTableSounds : ITableSounds
{
    public static readonly NullTableSounds Instance = new();
    public void Play(TableCue cue) { }
}

/// <summary>
/// What a move sounded like, worked out from the table before and after it — the same
/// for a move made here and for one that arrived from a shared table's server, since
/// both are only ever a table that changed. One sound for what the cards did (the most
/// telling of them), then points if any were scored, then a bell if it is now this
/// player's turn.
/// </summary>
public static class TableSounds
{
    /// <summary>Enough of a table to hear a move by: where each card is, the scores, whose turn.</summary>
    public sealed record Moment(
        IReadOnlyDictionary<int, (string Zone, string Type, bool FaceUp)> Cards,
        IReadOnlyDictionary<string, int> Scores,
        string? Turn,
        bool Over);

    public static Moment Capture(GameState state, bool over)
    {
        var cards = new Dictionary<int, (string, string, bool)>();
        foreach (var zone in state.Zones.Values)
            foreach (var card in zone.Cards)
                cards[card.Uid] = (zone.Id, zone.Type, card.IsFaceUp);
        return new Moment(cards, new Dictionary<string, int>(state.Scores),
                          state.Players.Count > 0 ? state.CurrentPlayer.Id : null, over);
    }

    public static IReadOnlyList<TableCue> Between(Moment before, GameState after, bool over, string viewer)
    {
        var cues = new List<TableCue>();

        if (over && !before.Over)
        {
            string winner = after.Metadata.GetValueOrDefault("last_winner") ?? "";
            bool mine = winner == viewer || after.GetPlayerTeam(viewer)?.Id == winner;
            cues.Add(mine ? TableCue.Win : TableCue.Lose);
            return cues;
        }

        var now = Capture(after, over);
        int intoHands = 0, played = 0, drawn = 0, gathered = 0, flipped = 0;
        foreach (var (uid, (zone, type, faceUp)) in now.Cards)
        {
            if (!before.Cards.TryGetValue(uid, out var was)) continue;
            if (was.Zone == zone)
            {
                if (faceUp && !was.FaceUp) flipped++;
                continue;
            }

            if (type == "hand")
            {
                intoHands++;
                if (was.Type is "deck" or "pile") drawn++;
            }
            else if (was.Type == "trick") gathered++;
            else if (was.Type == "hand") played++;
        }

        // The cards: a deal outweighs a draw, a trick gathered the last card played to it.
        if (intoHands > 4)      cues.Add(TableCue.Deal);
        else if (gathered > 0)  cues.Add(TableCue.Gather);
        else if (played > 0)    cues.Add(TableCue.Play);
        else if (drawn > 0)     cues.Add(TableCue.Draw);
        else if (flipped > 0)   cues.Add(TableCue.Flip);

        if (now.Scores.Any(kv => before.Scores.GetValueOrDefault(kv.Key) != kv.Value))
            cues.Add(TableCue.Score);

        if (now.Turn == viewer && before.Turn != viewer)
            cues.Add(TableCue.YourTurn);

        return cues;
    }
}
