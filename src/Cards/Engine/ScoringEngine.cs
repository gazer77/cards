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
///   euchre       — euchre team scoring (stub — implement with trick_taking).
///   hand_rank    — poker hand-rank winner (stub — implement with showdown).
///   deadwood     — Gin Rummy unmelded card values (stub — implement with meld).
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
            case "hand_rank":
            case "deadwood":
                // Not yet implemented — no score applied this round.
                state.Metadata["status"] = $"Scoring ({scoring.Type}) not yet implemented.";
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

        // Shoot the moon: if one player captured all point cards, add 26 to all others.
        if (shootMoon)
        {
            var special = scoring.Extra!["special"];
            var moonPlayer = roundScores
                .Where(kv => kv.Value == 26)
                .Select(kv => kv.Key)
                .FirstOrDefault();

            if (moonPlayer is not null)
            {
                string effect = GetSpecialEffect(special, "shoot_the_moon") ?? "add_26_to_others";
                if (effect == "subtract_26_from_self")
                {
                    roundScores[moonPlayer] = -26;
                    foreach (var p in state.Players.Where(p => p.Id != moonPlayer))
                        roundScores[p.Id] = 0;
                }
                else // add_26_to_others
                {
                    roundScores[moonPlayer] = 0;
                    foreach (var p in state.Players.Where(p => p.Id != moonPlayer))
                        roundScores[p.Id] = 26;
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
