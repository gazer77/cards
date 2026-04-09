using System.Text.Json;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Applies declarative scoring from a game definition's <c>scoring</c> block.
///
/// Implemented types:
///   none         — no scoring applied.
///   card_points  — sum card values from a zone by suit/card rules (Hearts).
///   trick_bid    — bid-vs-taken scoring with bags and nil bonuses (Spades).
///   grid_values  — sum grid card values with column-pair rules (Golf).
///   euchre       — euchre team scoring (maker/euchre/loner points).
///   deadwood     — Gin Rummy unmelded card values with knock/gin/undercut.
///   meld_points  — canasta-style meld scoring with canasta bonuses and go-out bonus.
///   pinochle     — trick-point scoring with bid-set penalty.
///   hand_rank    — poker hand-rank winner (handled by ShowdownHandler; no-op here).
///   blackjack    — chip tracking (handled by BlackjackLogic; no-op here).
/// </summary>
public static class ScoringEngine
{
    /// <summary>
    /// Computes round scores and applies them to <c>state.Scores</c>.
    /// Always writes a summary into <c>state.Metadata["score_summary"]</c>.
    /// </summary>
    public static void Apply(GameState state)
    {
        var scoring = state.Definition.Scoring;
        if (scoring is null) return;

        switch (scoring.Type)
        {
            case "none":
            case "blackjack":
                break;

            case "card_points":
                ApplyCardPoints(state, scoring);
                break;

            case "trick_bid":
                ApplyTrickBid(state, scoring);
                break;

            case "grid_values":
                ApplyGridValues(state, scoring);
                break;

            case "euchre":
                ApplyEuchre(state, scoring);
                break;

            case "deadwood":
                ApplyDeadwood(state, scoring);
                break;

            case "hand_rank":
                // Poker hand-rank scoring is handled by ShowdownHandler (pot distribution).
                // The score phase is not used in poker flows; this is a no-op fallback.
                break;

            case "meld_points":
                ApplyMeldPoints(state, scoring);
                break;

            case "pinochle":
                ApplyPinochle(state, scoring);
                break;
        }
    }

    // ── card_points ───────────────────────────────────────────────────────────

    private static void ApplyCardPoints(GameState state, ScoringDefinition scoring)
    {
        string countFrom  = GetString(scoring, "count_from") ?? "won_tricks";
        bool   accumulate = GetBool(scoring, "accumulate") ?? true;
        var    rules      = ParseCardValueRules(scoring);
        bool   shootMoon  = scoring.Extra?.ContainsKey("special") == true;

        // Compute raw score per player from their zone.
        var roundScores = new Dictionary<string, int>();
        foreach (var p in state.Players)
        {
            var zone = state.FindZone($"{countFrom}:{p.Id}") ?? state.FindZone(countFrom);
            if (zone is null) { roundScores[p.Id] = 0; continue; }

            int pts = 0;
            foreach (var card in zone.Cards)
                pts += rules.GetCardValue(card);
            roundScores[p.Id] = pts;
        }

        // Shoot the moon: if one player captured ALL point cards, redistribute.
        if (shootMoon)
        {
            var special      = scoring.Extra!["special"];
            int totalPoints  = roundScores.Values.Sum();
            string? moonPlayer = totalPoints > 0
                ? roundScores.FirstOrDefault(kv => kv.Value == totalPoints).Key
                : null;

            if (moonPlayer is not null)
            {
                string effect = GetSpecialEffect(special, "shoot_the_moon") ?? "add_26_to_others";
                if (effect == "subtract_26_from_self")
                {
                    roundScores[moonPlayer] = -totalPoints;
                    foreach (var p in state.Players.Where(p => p.Id != moonPlayer))
                        roundScores[p.Id] = 0;
                }
                else // add_26_to_others
                {
                    roundScores[moonPlayer] = 0;
                    foreach (var p in state.Players.Where(p => p.Id != moonPlayer))
                        roundScores[p.Id] = totalPoints;
                }
            }
        }

        foreach (var (pid, pts) in roundScores)
        {
            if (accumulate) state.AddScore(pid, pts);
            else            state.Scores[pid] = pts;
        }

        WriteSummary(state, roundScores);
    }

    // ── trick_bid ─────────────────────────────────────────────────────────────
    // Spades-style: score bid tricks × per_bid_trick, bag penalties, nil bonuses.
    // Bids and trick counts are read from metadata set by the bidding/trick_taking phases.
    // Keys: "bid:{playerId}" and "tricks_taken:{playerId}".
    // When count_by == "team", non-nil bids/tricks are aggregated per team and the
    // team score is stored under the team ID (e.g. "team0") in state.Scores.

    private static void ApplyTrickBid(GameState state, ScoringDefinition scoring)
    {
        int  perBidTrick = GetInt(scoring, "per_bid_trick") ?? 10;
        bool countByTeam = string.Equals(GetString(scoring, "count_by"), "team",
                               StringComparison.OrdinalIgnoreCase)
                           && state.Teams.Count > 0;
        bool accumulate  = GetBool(scoring, "accumulate") ?? true;

        int?  bagsPerPenalty = null;
        int   bagPenalty     = 0;
        if (scoring.Extra?.TryGetValue("bag_penalty", out var bagEl) == true
            && bagEl.ValueKind == JsonValueKind.Object
            && bagEl.ValueKind != System.Text.Json.JsonValueKind.Null)
        {
            bagsPerPenalty = bagEl.TryGetProperty("bags_per_penalty", out var bpp) ? bpp.GetInt32() : 10;
            bagPenalty     = bagEl.TryGetProperty("penalty", out var pen) ? pen.GetInt32() : -100;
        }

        int nilSuccess   = GetNestedInt(scoring, "nil",       "success") ?? 100;
        int nilFailure   = GetNestedInt(scoring, "nil",       "failure") ?? -100;
        int blindSuccess = GetNestedInt(scoring, "blind_nil", "success") ?? 200;
        int blindFailure = GetNestedInt(scoring, "blind_nil", "failure") ?? -200;

        var roundScores = new Dictionary<string, int>();

        // ── Step 1: per-player nil / blind-nil bonuses (always individual) ────
        foreach (var p in state.Players)
        {
            string bid   = state.Metadata.GetValueOrDefault($"bid:{p.Id}", "0");
            bool isNil   = string.Equals(bid, "nil",       StringComparison.OrdinalIgnoreCase);
            bool isBlind = string.Equals(bid, "blind_nil", StringComparison.OrdinalIgnoreCase);
            if (!isNil && !isBlind) continue;

            int taken     = int.TryParse(state.Metadata.GetValueOrDefault($"tricks_taken:{p.Id}", "0"), out int tt) ? tt : 0;
            bool success  = taken == 0;
            int  bonus    = isBlind
                ? (success ? blindSuccess : blindFailure)
                : (success ? nilSuccess   : nilFailure);

            string scoreKey = countByTeam
                ? (state.GetPlayerTeam(p.Id)?.Id ?? p.Id)
                : p.Id;
            roundScores[scoreKey] = roundScores.GetValueOrDefault(scoreKey) + bonus;
        }

        // ── Step 2: bid scoring — by team or by player ────────────────────────
        if (countByTeam)
        {
            foreach (var team in state.Teams)
            {
                int teamBid   = 0;
                int teamTaken = 0;

                foreach (var pid in team.PlayerIds)
                {
                    string bid = state.Metadata.GetValueOrDefault($"bid:{pid}", "0");
                    if (string.Equals(bid, "nil",       StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.Equals(bid, "blind_nil", StringComparison.OrdinalIgnoreCase)) continue;
                    teamBid   += int.TryParse(bid,                                                              out int b) ? b : 0;
                    teamTaken += int.TryParse(state.Metadata.GetValueOrDefault($"tricks_taken:{pid}", "0"), out int t) ? t : 0;
                }

                int pts = 0;
                if (teamTaken >= teamBid)
                {
                    pts += teamBid * perBidTrick;
                    int overtricks = teamTaken - teamBid;
                    pts += overtricks;
                    if (bagsPerPenalty.HasValue)
                    {
                        int existingBags    = GetBags(state, team.Id);
                        int newBags         = existingBags + overtricks;
                        SetBags(state, team.Id, newBags);
                        int penaltiesEarned = newBags / bagsPerPenalty.Value
                                           - existingBags / bagsPerPenalty.Value;
                        pts += penaltiesEarned * bagPenalty;
                    }
                }
                else
                {
                    pts -= teamBid * perBidTrick;
                }

                roundScores[team.Id] = roundScores.GetValueOrDefault(team.Id) + pts;
            }
        }
        else
        {
            foreach (var p in state.Players)
            {
                string bid   = state.Metadata.GetValueOrDefault($"bid:{p.Id}", "0");
                bool isNil   = string.Equals(bid, "nil",       StringComparison.OrdinalIgnoreCase);
                bool isBlind = string.Equals(bid, "blind_nil", StringComparison.OrdinalIgnoreCase);
                if (isNil || isBlind) continue; // handled above

                int bidNum   = int.TryParse(bid,                                                              out int b) ? b : 0;
                int takenNum = int.TryParse(state.Metadata.GetValueOrDefault($"tricks_taken:{p.Id}", "0"), out int t) ? t : 0;
                int pts      = 0;

                if (takenNum >= bidNum)
                {
                    pts += bidNum * perBidTrick;
                    int overtricks = takenNum - bidNum;
                    pts += overtricks;
                    if (bagsPerPenalty.HasValue)
                    {
                        int existingBags    = GetBags(state, p.Id);
                        int newBags         = existingBags + overtricks;
                        SetBags(state, p.Id, newBags);
                        int penaltiesEarned = newBags / bagsPerPenalty.Value
                                           - existingBags / bagsPerPenalty.Value;
                        pts += penaltiesEarned * bagPenalty;
                    }
                }
                else
                {
                    pts -= bidNum * perBidTrick;
                }

                roundScores[p.Id] = roundScores.GetValueOrDefault(p.Id) + pts;
            }
        }

        // Apply scores.
        foreach (var (id, pts) in roundScores)
        {
            if (accumulate) state.AddScore(id, pts);
            else            state.Scores[id] = pts;
        }

        WriteSummary(state, roundScores);
    }

    // ── grid_values ───────────────────────────────────────────────────────────

    private static void ApplyGridValues(GameState state, ScoringDefinition scoring)
    {
        bool accumulate      = GetBool(scoring, "accumulate") ?? true;
        int  faceDownPenalty = GetInt(scoring, "face_down_penalty") ?? 2;
        var  rules           = ParseGridValueRules(scoring);

        int? pairValue = null;
        if (scoring.Extra?.TryGetValue("matching_columns", out var mcEl) == true
            && mcEl.ValueKind == JsonValueKind.Object
            && mcEl.TryGetProperty("pair_value", out var pv))
            pairValue = pv.GetInt32();

        var roundScores = new Dictionary<string, int>();

        foreach (var p in state.Players)
        {
            var zone = state.FindZone($"grid:{p.Id}") ?? state.FindZone("grid");
            if (zone is null) { roundScores[p.Id] = 0; continue; }

            var cards = zone.Cards.ToList();
            int rows  = GetZoneDef(state, "grid")?.Rows ?? 2;
            int cols  = GetZoneDef(state, "grid")?.Cols ?? 3;

            int pts = 0;
            // Determine column pairs if pair_value is configured.
            var colPairs = new HashSet<int>();
            if (pairValue.HasValue)
            {
                for (int c = 0; c < cols; c++)
                {
                    var colCards = Enumerable.Range(0, rows)
                        .Select(r => r * cols + c)
                        .Where(i => i < cards.Count)
                        .Select(i => cards[i])
                        .Where(card => card.IsFaceUp)
                        .ToList();
                    if (colCards.Count == rows && colCards.Select(card => card.Rank).Distinct().Count() == 1)
                        colPairs.Add(c);
                }
            }

            for (int i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (!card.IsFaceUp)
                {
                    pts += faceDownPenalty;
                    continue;
                }
                int col = i % cols;
                if (pairValue.HasValue && colPairs.Contains(col))
                    pts += pairValue.Value;
                else
                    pts += rules.GetGridValue(card);
            }

            roundScores[p.Id] = pts;
        }

        foreach (var (pid, pts) in roundScores)
        {
            if (accumulate) state.AddScore(pid, pts);
            else            state.Scores[pid] = pts;
        }

        WriteSummary(state, roundScores);
    }

    // ── euchre ────────────────────────────────────────────────────────────────
    // Maker's team wins 1 pt (3-4 tricks), 2 pts (5 tricks), or 4 pts (loner 5).
    // Euchre (< 3 tricks) awards 2 pts to the opposing team.

    private static void ApplyEuchre(GameState state, ScoringDefinition scoring)
    {
        bool accumulate  = GetBool(scoring, "accumulate") ?? true;
        bool countByTeam = string.Equals(GetString(scoring, "count_by"), "team",
                               StringComparison.OrdinalIgnoreCase)
                           && state.Teams.Count > 0;

        // makers_win supports both "tricks_3_or_4" (4p) and "tricks_3_to_5" (3p unified key).
        int? raw34   = GetNestedInt(scoring, "makers_win", "tricks_3_or_4");
        int? raw3to5 = GetNestedInt(scoring, "makers_win", "tricks_3_to_5");
        int? raw5    = GetNestedInt(scoring, "makers_win", "tricks_5");
        int makers3or4 = raw34 ?? raw3to5 ?? 1;
        // If tricks_3_to_5 used without a separate tricks_5, sweeping scores the same.
        int makers5    = raw5 ?? (raw3to5.HasValue ? raw3to5.Value : 2);

        // euchred: "opponents_score" (team/single key) or "opponents_each_score" (all opponents individually).
        int oppoEachPts = GetNestedInt(scoring, "euchred", "opponents_each_score") ?? 0;
        int euchredPts  = GetNestedInt(scoring, "euchred", "opponents_score")      ?? (oppoEachPts > 0 ? 0 : 2);

        // loner_win: null in JSON means no loner scoring (0 pts).
        bool lonerDisabled = scoring.Extra?.TryGetValue("loner_win", out var lv) == true
                             && lv.ValueKind == JsonValueKind.Null;
        int loner5 = lonerDisabled ? 0 : (GetNestedInt(scoring, "loner_win", "tricks_5") ?? 4);

        string makerId   = state.Metadata.GetValueOrDefault("euchre_maker", "");
        var    maker     = state.Players.FirstOrDefault(p => p.Id == makerId);
        if (maker is null) return;

        bool goingAlone = state.Metadata.GetValueOrDefault($"bid_alone:{makerId}") == "true";

        Team? makerTeam   = countByTeam ? state.GetPlayerTeam(makerId) : null;
        string makerKey   = makerTeam?.Id ?? makerId;
        string? oppoKey   = makerTeam is not null
            ? state.Teams.FirstOrDefault(t => t.Id != makerTeam.Id)?.Id
            : state.Players.FirstOrDefault(p => p.Id != makerId)?.Id;

        // Count maker-side tricks.
        int makerTricks = 0;
        if (makerTeam is not null)
        {
            foreach (var pid in makerTeam.PlayerIds)
            {
                if (goingAlone && pid != makerId) continue;
                makerTricks += GetTricksTaken(state, pid);
            }
        }
        else
        {
            makerTricks = GetTricksTaken(state, makerId);
        }

        var roundScores = new Dictionary<string, int>();
        if (makerTricks >= 5)
            roundScores[makerKey] = goingAlone && loner5 > 0 ? loner5 : makers5;
        else if (makerTricks >= 3)
            roundScores[makerKey] = makers3or4;
        else
        {
            // Euchred: award to team key or each individual opponent.
            if (oppoEachPts > 0)
            {
                foreach (var p in state.Players)
                    if (p.Id != makerId) roundScores[p.Id] = oppoEachPts;
            }
            else if (oppoKey is not null && euchredPts > 0)
                roundScores[oppoKey] = euchredPts;
        }

        foreach (var (id, pts) in roundScores)
        {
            if (accumulate) state.AddScore(id, pts);
            else            state.Scores[id] = pts;
        }

        // Status line: "Euchred! Opponents +2" or "Makers win +2" etc.
        string euchredDisplay = oppoEachPts > 0
            ? $"+{oppoEachPts} each to opponents" : $"+{euchredPts} to opponents";
        string outcome = makerTricks >= 5
            ? (goingAlone ? $"Loner! +{(goingAlone ? loner5 : makers5)}" : $"Makers sweep! +{makers5}")
            : makerTricks >= 3
            ? $"Makers win ({makerTricks} tricks) +{makers3or4}"
            : $"Euchred! ({euchredDisplay})";

        // Build score summary per team.
        if (state.Teams.Count > 0)
        {
            var teamParts = state.Teams.Select(t =>
            {
                int total = state.GetTeamScore(t.Id);
                bool humanTeam = state.Players.Count > 0 && t.Contains(state.Players[0].Id);
                string label = humanTeam ? "Your team" : t.Name;
                return $"{label}: {total}";
            });
            string summary = outcome + "  |  " + string.Join("  |  ", teamParts);
            state.Metadata["status"]        = summary;
            state.Metadata["score_summary"] = summary;
        }
        else
        {
            WriteSummary(state, roundScores);
        }
    }

    private static int GetTricksTaken(GameState state, string playerId)
        => int.TryParse(state.Metadata.GetValueOrDefault($"tricks_taken:{playerId}", "0"),
                        out int t) ? t : 0;

    // ── deadwood ──────────────────────────────────────────────────────────────
    // Gin Rummy: score based on unmelded card values after knock/gin.

    private static void ApplyDeadwood(GameState state, ScoringDefinition scoring)
    {
        string knockerId = state.Metadata.GetValueOrDefault("dd_knock_player", "");
        bool   isGin     = state.Metadata.GetValueOrDefault("dd_gin", "false") == "true";
        bool   accumulate = GetBool(scoring, "accumulate") ?? true;

        int knockBonus    = GetInt(scoring, "knock_bonus")    ?? 25;
        int ginBonus      = GetInt(scoring, "gin_bonus")      ?? 25;
        int undercutBonus = GetInt(scoring, "undercut_bonus") ?? 25;

        var values = ParseDeadwoodValues(scoring);

        // Find knocker and opponent.
        var knocker  = state.Players.FirstOrDefault(p => p.Id == knockerId);
        var opponent = state.Players.FirstOrDefault(p => p.Id != knockerId);
        if (knocker is null || opponent is null) return;

        int knockerDW  = CalcMinDeadwood(GetHandCards(state, knocker.Id),  values);
        int opponentDW = CalcMinDeadwood(GetHandCards(state, opponent.Id), values);

        var roundScores = new Dictionary<string, int>();

        if (isGin)
        {
            // Gin: knocker wins opponent's deadwood + gin bonus
            roundScores[knocker.Id] = opponentDW + ginBonus;
        }
        else if (knockerDW < opponentDW)
        {
            // Knock: knocker wins difference + knock bonus
            roundScores[knocker.Id] = (opponentDW - knockerDW) + knockBonus;
        }
        else
        {
            // Undercut: opponent wins difference + undercut bonus
            roundScores[opponent.Id] = (knockerDW - opponentDW) + undercutBonus;
        }

        foreach (var (pid, pts) in roundScores)
        {
            if (accumulate) state.AddScore(pid, pts);
            else            state.Scores[pid] = pts;
        }

        // Status summary
        string kLabel = knocker  == state.Players[0] ? "You" : knocker.Name;
        string oLabel = opponent == state.Players[0] ? "You" : opponent.Name;
        string summary;
        if (isGin)
            summary = $"Gin! {kLabel}: 0 dw | {oLabel}: {opponentDW} dw  → +{ginBonus + opponentDW}";
        else if (roundScores.ContainsKey(knocker.Id))
            summary = $"Knock! {kLabel}: {knockerDW} dw | {oLabel}: {opponentDW} dw  → +{roundScores[knocker.Id]}";
        else
            summary = $"Undercut! {oLabel}: {opponentDW} dw | {kLabel}: {knockerDW} dw  → +{roundScores[opponent.Id]}";

        state.Metadata["status"]        = summary;
        state.Metadata["score_summary"] = summary;

        // Overall totals
        string totals = string.Join("  |  ", state.Players.Select(p =>
        {
            string label = p == state.Players[0] ? "You" : p.Name;
            return $"{label}: {state.GetScore(p.Id)}";
        }));
        state.Metadata["score_summary"] = summary + "  ||  " + totals;
    }

    private static IReadOnlyList<Card> GetHandCards(GameState state, string playerId)
    {
        var hand = state.FindZone($"hand:{playerId}") ?? state.FindZone("hand");
        return hand?.Cards ?? [];
    }

    /// <summary>
    /// Public helper: computes minimum deadwood for <paramref name="hand"/> using
    /// standard Gin Rummy values (A=1, J/Q/K=10, pip otherwise).
    /// Used by <see cref="DrawDiscardHandler"/> to gate knock/gin actions.
    /// </summary>
    public static int CalcDeadwood(IReadOnlyList<Card> hand)
        => CalcMinDeadwood(hand, new DeadwoodValues());

    /// <summary>
    /// Returns the minimum deadwood total for a hand by finding the optimal
    /// non-overlapping set of melds (sets of same rank, runs of same suit).
    /// </summary>
    private static int CalcMinDeadwood(IReadOnlyList<Card> hand, DeadwoodValues values)
    {
        if (hand.Count == 0) return 0;

        var melds = FindAllMelds(hand);
        int totalValue = hand.Sum(c => values.GetValue(c));
        int bestMelded = FindBestMelds(melds, new bool[hand.Count], hand, values, 0);
        return Math.Max(0, totalValue - bestMelded);
    }

    private static List<List<int>> FindAllMelds(IReadOnlyList<Card> hand)
    {
        var result = new List<List<int>>();
        int n = hand.Count;

        // Sets: 3+ cards of the same rank.
        var byRank = Enumerable.Range(0, n).GroupBy(i => hand[i].Rank);
        foreach (var group in byRank)
        {
            var idx = group.ToList();
            if (idx.Count >= 3) result.Add(idx);
            if (idx.Count == 4)
            {
                // Also add each 3-card subset.
                for (int skip = 0; skip < 4; skip++)
                    result.Add(idx.Where((_, j) => j != skip).ToList());
            }
        }

        // Runs: 3+ consecutive ranks of the same suit.
        var bySuit = Enumerable.Range(0, n).GroupBy(i => hand[i].Suit);
        foreach (var group in bySuit)
        {
            var sorted = group.OrderBy(i => (int)hand[i].Rank).ToList();
            for (int start = 0; start < sorted.Count; start++)
            {
                var run = new List<int> { sorted[start] };
                for (int k = start + 1; k < sorted.Count; k++)
                {
                    if ((int)hand[sorted[k]].Rank == (int)hand[run[^1]].Rank + 1)
                        run.Add(sorted[k]);
                    else
                        break;
                }
                for (int len = 3; len <= run.Count; len++)
                    result.Add(run.Take(len).ToList());
            }
        }

        return result;
    }

    private static int FindBestMelds(
        List<List<int>> melds, bool[] used, IReadOnlyList<Card> hand,
        DeadwoodValues values, int idx)
    {
        if (idx >= melds.Count) return 0;

        // Skip this meld.
        int best = FindBestMelds(melds, used, hand, values, idx + 1);

        // Try using this meld if no card overlaps.
        var meld = melds[idx];
        if (!meld.Any(i => used[i]))
        {
            foreach (int i in meld) used[i] = true;
            int meldValue = meld.Sum(i => values.GetValue(hand[i]));
            int withMeld  = meldValue + FindBestMelds(melds, used, hand, values, idx + 1);
            best = Math.Max(best, withMeld);
            foreach (int i in meld) used[i] = false;
        }

        return best;
    }

    private static DeadwoodValues ParseDeadwoodValues(ScoringDefinition scoring)
    {
        var dv = new DeadwoodValues();
        if (scoring.Extra?.TryGetValue("card_values", out var el) != true
            || el.ValueKind != JsonValueKind.Object) return dv;

        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number) continue;
            int v = prop.Value.GetInt32();
            switch (prop.Name.ToUpperInvariant())
            {
                case "A":       dv.Ace    = v; break;
                case "J":       dv.Jack   = v; break;
                case "Q":       dv.Queen  = v; break;
                case "K":       dv.King   = v; break;
                case "DEFAULT": dv.UsePip = false; dv.DefaultVal = v; break;
                default:
                    if (int.TryParse(prop.Name, out int rank) && rank >= 2 && rank <= 10)
                        dv.RankValues[rank] = v;
                    break;
            }
        }
        return dv;
    }

    private sealed class DeadwoodValues
    {
        public int  Ace        = 1;
        public int  Jack       = 10;
        public int  Queen      = 10;
        public int  King       = 10;
        public bool UsePip     = true;
        public int  DefaultVal = 0;
        public Dictionary<int, int> RankValues = [];

        public int GetValue(Card c)
        {
            if (c.Rank == Rank.Ace)   return Ace;
            if (c.Rank == Rank.Jack)  return Jack;
            if (c.Rank == Rank.Queen) return Queen;
            if (c.Rank == Rank.King)  return King;
            int r = (int)c.Rank;
            if (RankValues.TryGetValue(r, out int v)) return v;
            return UsePip ? r : DefaultVal;
        }
    }

    // ── meld_points ───────────────────────────────────────────────────────────
    // Hand and Foot / Canasta: score meld zones, detect canastas, deduct hand cards,
    // award go-out bonus to the team/player who went out.

    private static void ApplyMeldPoints(GameState state, ScoringDefinition scoring)
    {
        bool accumulate     = GetBool(scoring, "accumulate") ?? true;
        bool countByTeam    = string.Equals(GetString(scoring, "count_by"), "team",
                                  StringComparison.OrdinalIgnoreCase)
                              && state.Teams.Count > 0;
        int natCanBonus     = GetInt(scoring, "natural_canasta_bonus") ?? 500;
        int wildCanBonus    = GetInt(scoring, "wild_canasta_bonus")    ?? 1000;
        int goOutBonus      = GetInt(scoring, "go_out_bonus")          ?? 100;
        var cardValues      = ParseMeldCardValues(scoring);
        var wildCards       = ParseMeldWildCards(scoring);

        string? goOutPlayer = state.Metadata.GetValueOrDefault("dd_go_out_player");
        string? goOutTeam   = state.Metadata.GetValueOrDefault("dd_go_out_team");

        // red_three_penalty: applied per Three held in hand at end of round.
        // Card-values "3": negative value is for meld zone scoring; hand deduction
        // uses this positive penalty instead to avoid a sign-inversion bonus.
        int redThreePenalty = Math.Abs(GetInt(scoring, "red_three_penalty") ?? 0);

        // Local helper: card value used for hand/foot deductions.
        // Uses redThreePenalty for Threes (avoids sign-inversion from negative card_values["3"]).
        // Clamps other values to ≥0 so no card in hand ever produces a scoring bonus.
        int HandCardPenalty(Card c) => c.Rank == Rank.Three
            ? redThreePenalty
            : Math.Max(0, cardValues.GetValue(c));

        var scores = new Dictionary<string, int>();

        if (countByTeam)
        {
            foreach (var team in state.Teams)
            {
                int pts = 0;

                // Meld zone for this team.
                var meldZone = state.FindZone($"meld:{team.Id}");
                if (meldZone is not null)
                {
                    pts += ScoreMeldZone(meldZone, cardValues, wildCards, natCanBonus, wildCanBonus);
                }

                // Deduct card values for cards remaining in each player's hand.
                foreach (var pid in team.PlayerIds)
                {
                    var hand = state.FindZone($"hand:{pid}") ?? state.FindZone("hand");
                    if (hand is not null)
                        pts -= hand.Cards.Sum(c => HandCardPenalty(c));
                    // Also deduct foot zone if present.
                    var foot = state.FindZone($"foot:{pid}");
                    if (foot is not null)
                        pts -= foot.Cards.Sum(c => HandCardPenalty(c));
                }

                // Go-out bonus.
                if (goOutTeam == team.Id)
                    pts += goOutBonus;

                scores[team.Id] = pts;
            }
        }
        else
        {
            foreach (var p in state.Players)
            {
                int pts = 0;

                var meldZone = state.FindZone($"meld:{p.Id}") ?? state.FindZone("meld");
                if (meldZone is not null)
                    pts += ScoreMeldZone(meldZone, cardValues, wildCards, natCanBonus, wildCanBonus);

                var hand = state.FindZone($"hand:{p.Id}") ?? state.FindZone("hand");
                if (hand is not null)
                    pts -= hand.Cards.Sum(c => HandCardPenalty(c));

                if (goOutPlayer == p.Id)
                    pts += goOutBonus;

                scores[p.Id] = pts;
            }
        }

        foreach (var (id, pts) in scores)
        {
            if (accumulate) state.AddScore(id, pts);
            else            state.Scores[id] = pts;
        }

        // Build status summary.
        string goOutLabel = goOutPlayer is not null
            ? state.Players.FirstOrDefault(p => p.Id == goOutPlayer)?.Name ?? "Someone"
            : "";
        string outPart = goOutLabel.Length > 0 ? $"{goOutLabel} went out! " : "";
        string summary = outPart + string.Join("  |  ", scores.Select(kv =>
        {
            string label = countByTeam
                ? (state.Teams.FirstOrDefault(t => t.Id == kv.Key) is { } t
                    ? (t.Contains(state.Players[0].Id) ? "Your team" : t.Name)
                    : kv.Key)
                : (state.Players.FirstOrDefault(p => p.Id == kv.Key) is { } pl
                    ? (pl == state.Players[0] ? "You" : pl.Name)
                    : kv.Key);
            int total = state.GetScore(kv.Key);
            return $"{label}: +{kv.Value} = {total}";
        }));
        state.Metadata["status"]        = summary;
        state.Metadata["score_summary"] = summary;
    }

    /// <summary>
    /// Scores cards in a meld zone: card point values, natural canasta bonuses,
    /// and wild canasta bonuses.
    ///
    /// Canasta detection: wild cards (jokers/twos) supplement the largest natural
    /// rank groups.  A pile of 4 Aces + 3 wild Twos = 7-card mixed canasta.
    /// Standard limit of 3 wilds per canasta is enforced.
    /// </summary>
    private static int ScoreMeldZone(
        Zone zone, MeldCardValues values, HashSet<string> wildCards,
        int natCanBonus, int wildCanBonus)
    {
        int pts = zone.Cards.Sum(c => values.GetValue(c));

        bool IsWildCard(Card c) =>
            c.IsWild ||
            wildCards.Contains(c.Rank.ToString().ToLower()) ||
            (c.Rank == Rank.Two && wildCards.Contains("2")) ||
            wildCards.Contains(c.Rank == Rank.Ace ? "a" : ((int)c.Rank).ToString());

        const int maxWildsPerCanasta = 3;

        // Separate wilds from naturals.
        int wildsRemaining = zone.Cards.Count(IsWildCard);
        var naturalGroups  = zone.Cards
            .Where(c => !IsWildCard(c))
            .GroupBy(c => c.Rank)
            .OrderByDescending(g => g.Count())
            .ToList();

        // Assign wilds to complete natural groups into canastas (greedy, largest first).
        foreach (var group in naturalGroups)
        {
            int count    = group.Count();
            int need     = Math.Max(0, 7 - count);
            int canUse   = Math.Min(need, Math.Min(wildsRemaining, maxWildsPerCanasta));

            if (count + canUse < 7) continue;  // can't reach canasta size

            wildsRemaining -= canUse;
            pts += canUse > 0 ? wildCanBonus : natCanBonus;
        }

        // Pure wild canasta (7+ wilds with no natural cards assigned above).
        while (wildsRemaining >= 7)
        {
            wildsRemaining -= 7;
            pts += wildCanBonus;
        }

        return pts;
    }

    private static MeldCardValues ParseMeldCardValues(ScoringDefinition scoring)
    {
        var dv = new MeldCardValues();
        if (scoring.Extra?.TryGetValue("card_values", out var el) != true
            || el.ValueKind != JsonValueKind.Object) return dv;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number) continue;
            int v = prop.Value.GetInt32();
            switch (prop.Name.ToLowerInvariant())
            {
                case "joker":   dv.Joker      = v; break;
                case "a":       dv.Ace        = v; break;
                case "k":       dv.King       = v; break;
                case "q":       dv.Queen      = v; break;
                case "j":       dv.Jack       = v; break;
                case "default": dv.DefaultVal = v; break;
                default:
                    if (int.TryParse(prop.Name, out int rank) && rank >= 2 && rank <= 10)
                        dv.RankValues[rank] = v;
                    break;
            }
        }
        return dv;
    }

    private static HashSet<string> ParseMeldWildCards(ScoringDefinition scoring)
    {
        var wilds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (scoring.Extra?.TryGetValue("wild_cards", out var el) != true
            || el.ValueKind != JsonValueKind.Array) return wilds;
        foreach (var item in el.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s)
                wilds.Add(s);
        return wilds;
    }

    private sealed class MeldCardValues
    {
        public int  Joker      = 50;
        public int  Ace        = 20;
        public int  King       = 10;
        public int  Queen      = 10;
        public int  Jack       = 10;
        public int  DefaultVal = 5;
        public Dictionary<int, int> RankValues = [];

        public int GetValue(Card c)
        {
            if (c.Rank == Rank.Joker || c.IsWild) return Joker;
            if (c.Rank == Rank.Ace)   return Ace;
            if (c.Rank == Rank.King)  return King;
            if (c.Rank == Rank.Queen) return Queen;
            if (c.Rank == Rank.Jack)  return Jack;
            int r = (int)c.Rank;
            if (RankValues.TryGetValue(r, out int v)) return v;
            return DefaultVal;
        }
    }

    // ── pinochle ───────────────────────────────────────────────────────────────
    // Trick points from won_tricks zones (per team).
    // Maker must meet bid or gets set (loses bid amount).
    // Meld points are not re-counted here — they are scored during the meld phase
    // via MeldHandler and added to state.Scores directly by that handler.
    // (If meld_score:{playerId} metadata is present it is also summed here.)

    private static void ApplyPinochle(GameState state, ScoringDefinition scoring)
    {
        bool accumulate    = GetBool(scoring, "accumulate") ?? true;
        int  lastTrickBonus = GetInt(scoring, "last_trick_bonus") ?? 10;
        bool bidSetPenalty  = string.Equals(GetString(scoring, "bid_set_penalty"), "bid_amount",
                                  StringComparison.OrdinalIgnoreCase);

        // Parse per-rank trick point values.
        var trickPts = ParsePinochleTrickValues(scoring);

        string? bidderId = GetHighBidder(state);
        Team?   makerTeam = bidderId is not null ? state.GetPlayerTeam(bidderId) : null;
        int     bid = bidderId is not null
            ? int.TryParse(state.Metadata.GetValueOrDefault($"bid:{bidderId}"), out int b) ? b : 0
            : 0;

        // Find who took the last trick.
        string? lastTrickWinnerId = state.Metadata.GetValueOrDefault("trick_last_winner");

        var roundScores = new Dictionary<string, int>();

        foreach (var team in state.Teams)
        {
            var wonZone = state.FindZone($"won_tricks:{team.Id}");
            int pts = 0;
            if (wonZone is not null)
            {
                foreach (var card in wonZone.Cards)
                    pts += trickPts.GetValue(card);
            }

            // Last trick bonus.
            if (lastTrickWinnerId is not null)
            {
                var lastTeam = state.GetPlayerTeam(lastTrickWinnerId);
                if (lastTeam?.Id == team.Id) pts += lastTrickBonus;
            }

            // Add any meld points stored in metadata by MeldHandler.
            foreach (var pid in team.PlayerIds)
            {
                if (state.Metadata.TryGetValue($"meld_score:{pid}", out var ms)
                    && int.TryParse(ms, out int meld))
                    pts += meld;
            }

            roundScores[team.Id] = pts;
        }

        // Bid-set check: maker must meet or exceed bid.
        bool bidMade = true;
        if (makerTeam is not null && bid > 0)
        {
            int makerPts = roundScores.GetValueOrDefault(makerTeam.Id);
            bidMade = makerPts >= bid;
            if (!bidMade && bidSetPenalty)
                roundScores[makerTeam.Id] = -bid;
        }

        foreach (var (id, pts) in roundScores)
        {
            if (accumulate) state.AddScore(id, pts);
            else            state.Scores[id] = pts;
        }

        // Status line.
        bool wasMade = makerTeam is null || bidMade;
        string outcome = makerTeam is null ? ""
            : wasMade ? $"Bid of {bid} made!  " : $"Set! Bid of {bid} lost.  ";

        string teamSummary = string.Join("  |  ", state.Teams.Select(t =>
        {
            bool humanTeam = state.Players.Count > 0 && t.Contains(state.Players[0].Id);
            string label = humanTeam ? "Your team" : t.Name;
            int total = state.GetTeamScore(t.Id);
            return $"{label}: {total}";
        }));

        string summary = outcome + teamSummary;
        state.Metadata["status"]        = summary;
        state.Metadata["score_summary"] = summary;
    }

    /// <summary>Returns the player who placed the highest numeric bid this round.</summary>
    private static string? GetHighBidder(GameState state)
    {
        string? bestId  = null;
        int     bestBid = -1;
        foreach (var p in state.Players)
        {
            if (state.Metadata.TryGetValue($"bid:{p.Id}", out var bidStr)
                && int.TryParse(bidStr, out int b) && b > bestBid)
            {
                bestBid = b;
                bestId  = p.Id;
            }
        }
        return bestId;
    }

    private static PinochleTrickValues ParsePinochleTrickValues(ScoringDefinition scoring)
    {
        var ptv = new PinochleTrickValues();
        if (scoring.Extra?.TryGetValue("trick_points", out var el) != true
            || el.ValueKind != JsonValueKind.Object) return ptv;
        foreach (var prop in el.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Number) continue;
            int v = prop.Value.GetInt32();
            switch (prop.Name.ToUpperInvariant())
            {
                case "A":  ptv.Ace   = v; break;
                case "10": ptv.Ten   = v; break;
                case "K":  ptv.King  = v; break;
                case "Q":  ptv.Queen = v; break;
                case "J":  ptv.Jack  = v; break;
                case "9":  ptv.Nine  = v; break;
            }
        }
        return ptv;
    }

    private sealed class PinochleTrickValues
    {
        public int Ace   = 11;
        public int Ten   = 10;
        public int King  = 4;
        public int Queen = 3;
        public int Jack  = 2;
        public int Nine  = 0;

        public int GetValue(Card c) => c.Rank switch
        {
            Rank.Ace   => Ace,
            Rank.Ten   => Ten,
            Rank.King  => King,
            Rank.Queen => Queen,
            Rank.Jack  => Jack,
            Rank.Nine  => Nine,
            _          => 0,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void WriteSummary(GameState state, Dictionary<string, int> roundScores)
    {
        var parts = state.Players.Select(p =>
        {
            int round = roundScores.GetValueOrDefault(p.Id);
            int total = state.GetScore(p.Id);
            string label = p == state.Players[0] ? "You" : p.Name;
            return round >= 0 ? $"{label}: +{round} = {total}" : $"{label}: {round} = {total}";
        });
        string summary = string.Join("  |  ", parts);
        state.Metadata["status"]        = summary;
        state.Metadata["score_summary"] = summary;
    }

    // ── Card value rule parsing ───────────────────────────────────────────────

    private static CardValueRules ParseCardValueRules(ScoringDefinition scoring)
    {
        var rules = new CardValueRules();
        if (scoring.Extra?.TryGetValue("card_values", out var el) != true) return rules;
        if (el.ValueKind != JsonValueKind.Array) return rules;

        foreach (var item in el.EnumerateArray())
        {
            int value = item.TryGetProperty("value", out var v) ? v.GetInt32() : 0;

            if (item.TryGetProperty("card", out var cardEl))
            {
                rules.CardRules[cardEl.GetString()!] = value;
            }
            else if (item.TryGetProperty("suit", out var suitEl))
            {
                rules.SuitRules[suitEl.GetString()!] = value;
            }
        }
        return rules;
    }

    private static GridValueRules ParseGridValueRules(ScoringDefinition scoring)
    {
        var rules = new GridValueRules();
        if (scoring.Extra?.TryGetValue("card_values", out var el) != true) return rules;
        if (el.ValueKind != JsonValueKind.Object) return rules;

        foreach (var prop in el.EnumerateObject())
        {
            string rankKey = prop.Name;
            if (rankKey == "default")
            {
                rules.DefaultMode = prop.Value.GetString() ?? "pip";
                continue;
            }
            if (prop.Value.ValueKind == JsonValueKind.Number)
                rules.RankValues[rankKey] = prop.Value.GetInt32();
        }
        return rules;
    }

    private static ZoneDefinition? GetZoneDef(GameState state, string zoneType)
        => state.Definition.Zones.FirstOrDefault(z => z.Type == zoneType);

    private static string? GetString(ScoringDefinition s, string key)
    {
        if (s.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static int? GetInt(ScoringDefinition s, string key)
    {
        if (s.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }

    private static bool? GetBool(ScoringDefinition s, string key)
    {
        if (s.Extra?.TryGetValue(key, out var el) == true &&
            el.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return el.GetBoolean();
        return null;
    }

    private static int? GetNestedInt(ScoringDefinition s, string outerKey, string innerKey)
    {
        if (s.Extra?.TryGetValue(outerKey, out var outer) == true
            && outer.ValueKind == JsonValueKind.Object
            && outer.TryGetProperty(innerKey, out var inner)
            && inner.ValueKind == JsonValueKind.Number)
            return inner.GetInt32();
        return null;
    }

    private static string? GetSpecialEffect(JsonElement specials, string name)
    {
        if (specials.ValueKind != JsonValueKind.Array) return null;
        foreach (var item in specials.EnumerateArray())
        {
            if (item.TryGetProperty("name", out var n) && n.GetString() == name
                && item.TryGetProperty("effect", out var e))
                return e.GetString();
        }
        return null;
    }

    private static int GetBags(GameState state, string playerId)
        => int.TryParse(state.Metadata.GetValueOrDefault($"bags:{playerId}", "0"), out int b) ? b : 0;

    private static void SetBags(GameState state, string playerId, int bags)
        => state.Metadata[$"bags:{playerId}"] = bags.ToString();

    // ── Inner helper classes ──────────────────────────────────────────────────

    private sealed class CardValueRules
    {
        public Dictionary<string, int> CardRules { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> SuitRules { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int GetCardValue(Card card)
        {
            // Specific card notation: "Qs" = Queen of Spades, "2h" = Two of Hearts
            string notation = $"{RankChar(card.Rank)}{SuitChar(card.Suit)}";
            if (CardRules.TryGetValue(notation, out int v)) return v;
            if (SuitRules.TryGetValue(card.Suit.ToString().ToLower(), out int sv)) return sv;
            return 0;
        }

        private static char RankChar(Rank r) => r switch
        {
            Rank.Ace   => 'A',
            Rank.King  => 'K',
            Rank.Queen => 'Q',
            Rank.Jack  => 'J',
            Rank.Ten   => 'T',
            _          => (char)('0' + (int)r),
        };

        private static char SuitChar(Suit s) => s switch
        {
            Suit.Spades   => 's',
            Suit.Hearts   => 'h',
            Suit.Diamonds => 'd',
            Suit.Clubs    => 'c',
            _             => '?',
        };
    }

    private sealed class GridValueRules
    {
        public Dictionary<string, int> RankValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string DefaultMode { get; set; } = "pip";

        public int GetGridValue(Card card)
        {
            string rankKey = card.Rank switch
            {
                Rank.Ace   => "A",
                Rank.King  => "K",
                Rank.Queen => "Q",
                Rank.Jack  => "J",
                Rank.Ten   => "10",
                _          => ((int)card.Rank).ToString(),
            };

            if (RankValues.TryGetValue(rankKey, out int v)) return v;
            if (RankValues.TryGetValue("joker", out int jv) && card.IsWild) return jv;
            return DefaultMode == "pip" ? (int)card.Rank : 0;
        }
    }
}
