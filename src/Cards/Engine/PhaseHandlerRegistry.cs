using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Maps phase type names to IPhaseHandler factories.
///
/// Built-in phase types cover common patterns that can be expressed
/// declaratively in a game definition with no custom C# required.
///
/// Built-in types:
///   flip_compare_ready   — each player reveals a card; highest rank wins the round.
///   flip_compare_result  — winner collects; advance round or end game.
///   name_trump           — bid winner selects a trump suit (writes bid_trump metadata).
/// </summary>
public static class PhaseHandlerRegistry
{
    private static readonly Dictionary<string, Func<PhaseDefinition, string, IPhaseHandler>> _factories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["flip_compare_ready"]  = (def, next) => new FlipCompareReadyHandler(def, next),
            ["flip_compare_result"] = (def, next) => new FlipCompareResultHandler(def, next),
            ["score"]               = (def, next) => new ScorePhaseHandler(next),
            ["trick_taking"]        = (def, next) => new TrickTakingHandler(def, next),
            ["bidding"]             = (def, next) => new BiddingHandler(def, next),
            ["pass_cards"]          = (def, next) => new PassCardsHandler(def, next),
            ["free_play"]           = (def, next) => new FreePlayHandler(def, next),
            ["draw_discard"]        = (def, next) => new DrawDiscardHandler(def, next),
            ["meld"]                = (def, next) => new MeldHandler(def, next),
            ["poker_betting"]       = (def, next) => new PokerBettingHandler(def, next),
            ["showdown"]            = (def, next) => new ShowdownHandler(def, next),
            ["war"]                 = (def, next) => new WarHandler(def, next),
            ["blackjack_round"]     = (def, next) => new BlackjackRoundHandler(def, next),
            ["go_fish"]             = (def, next) => new GoFishHandler(def, next),
            ["deal"]                = (def, next) => new DealPhaseHandler(def, next),
            ["name_trump"]          = (def, next) => new NameTrumpHandler(def, next),
        };

    /// <summary>
    /// Creates a handler for <paramref name="phaseDef"/>.
    /// <paramref name="nextPhaseId"/> is the phase to transition to when the
    /// round concludes (used by handlers that need to advance the game).
    /// Returns <c>null</c> when the phase type is not registered.
    /// </summary>
    public static IPhaseHandler? Create(PhaseDefinition phaseDef, string nextPhaseId)
        => _factories.TryGetValue(phaseDef.Type, out var factory)
            ? factory(phaseDef, nextPhaseId)
            : null;

    // ── flip_compare_ready ────────────────────────────────────────────────────
    // All players reveal their top hand card; highest rank wins the round.
    //
    // Phase definition parameters (all optional):
    //   tie_resolution  "split" (default) — each player takes back their own card.

    private sealed class FlipCompareReadyHandler(PhaseDefinition def, string resultPhaseId) : IPhaseHandler
    {
        private readonly bool _tieSplit =
            string.Equals(GetExtra(def, "tie_resolution") ?? "split", "split",
                StringComparison.OrdinalIgnoreCase);

        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

        public void Apply(GameState state, GameAction action)
        {
            if (action.Type != "tap") return;

            // If any hand is empty the game is decided by card count
            if (state.Players.Any(p => Hand(state, p).Count == 0))
            {
                ApplyWinResult(state, WinConditionEngine.Instance.Resolve(state));
                return;
            }

            // Flip: move top card from each hand to the play zone
            foreach (var p in state.Players)
                MoveTopCard(Hand(state, p), Play(state, p));

            CompareAndTransition(state);
        }

        private void CompareAndTransition(GameState state)
        {
            // Two-player comparison (standard for this phase type)
            var p0 = state.Players[0];
            var p1 = state.Players[1];
            var c0 = Play(state, p0).TopCard;
            var c1 = Play(state, p1).TopCard;

            if (c0 is null || c1 is null)
            {
                ApplyWinResult(state, WinConditionEngine.Instance.Resolve(state));
                return;
            }

            int r0 = (int)c0.Rank;
            int r1 = (int)c1.Rank;

            if (r0 > r1) { SetRoundResult(state, p0.Id, "You win this round!");     return; }
            if (r1 > r0) { SetRoundResult(state, p1.Id, "Opponent wins this round."); return; }

            // Tie — split: each player takes back their own card
            Hand(state, p0).Add(Play(state, p0).Draw()!);
            Hand(state, p1).Add(Play(state, p1).Draw()!);
            state.CurrentPhaseId          = resultPhaseId;
            state.Metadata["last_winner"] = "";
            state.Metadata["status"]      = "Tie! Cards returned.\nTap to continue.";
        }

        private void SetRoundResult(GameState state, string winnerId, string msg)
        {
            state.Metadata["last_winner"] = winnerId;
            state.Metadata["status"]      = msg + "\nTap to collect.";
            state.CurrentPhaseId          = resultPhaseId;
        }
    }

    // ── flip_compare_result ───────────────────────────────────────────────────
    // The round's winner collects all played cards, then the game advances.

    private sealed class FlipCompareResultHandler(PhaseDefinition _, string readyPhaseId) : IPhaseHandler
    {
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

        public void Apply(GameState state, GameAction action)
        {
            if (action.Type != "tap") return;

            string winnerId = state.Metadata.GetValueOrDefault("last_winner", "");
            if (!string.IsNullOrEmpty(winnerId))
            {
                var dest = state.Zones[$"hand:{winnerId}"];
                foreach (var p in state.Players)
                    DrainInto(Play(state, p), dest);
                if (state.Zones.TryGetValue("pot", out var pot))
                    DrainInto(pot, dest);
            }

            var winResult = WinConditionEngine.Instance.Check(state);
            if (winResult is not null) { ApplyWinResult(state, winResult); return; }

            state.CurrentPhaseId = readyPhaseId;
            state.RoundNumber++;
            state.Metadata["status"] = "Tap to flip!";
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static void ApplyWinResult(GameState state, WinResult result)
    {
        state.CurrentPhaseId          = "game_over";
        state.Metadata["last_winner"] = result.WinnerId ?? "";
        state.Metadata["status"]      = result.StatusMessage;
    }

    private static Zone Hand(GameState s, Player p) => s.Zones[$"hand:{p.Id}"];
    private static Zone Play(GameState s, Player p) => s.Zones[$"play:{p.Id}"];

    private static void MoveTopCard(Zone from, Zone to)
    {
        var c = from.Draw();
        if (c is not null) { c.IsFaceUp = true; to.Add(c); }
    }

    private static void DrainInto(Zone from, Zone to)
    {
        while (from.Count > 0) to.Add(from.Draw()!);
    }

    private static string? GetExtra(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true)
            return el.GetString();
        return null;
    }

    // ── name_trump ────────────────────────────────────────────────────────────
    // The bid winner (recorded in bid_winner metadata) selects a trump suit.
    // Writes bid_trump metadata, then advances to the next phase.
    //
    // Phase definition parameters:
    //   exclude_suit  — "bid_excluded_suit" | literal suit to exclude (optional)

    private sealed class NameTrumpHandler : IPhaseHandler
    {
        private readonly string  _nextPhaseId;
        private readonly string? _excludeSuit;
        private static readonly string[] AllSuits = ["clubs", "diamonds", "hearts", "spades"];

        public NameTrumpHandler(PhaseDefinition def, string nextPhaseId)
        {
            _nextPhaseId = nextPhaseId;
            _excludeSuit = GetExtra(def, "exclude_suit");
        }

        public IReadOnlyList<GameAction> GetValidActions(GameState state)
        {
            EnsureCurrentPlayerIsBidWinner(state);
            string? excluded = ResolveExclude(state);
            return AllSuits
                .Where(s => s != excluded)
                .Select(s => new GameAction($"trump_{s}", Label: Capitalize(s)))
                .ToList<GameAction>();
        }

        public void Apply(GameState state, GameAction action)
        {
            EnsureCurrentPlayerIsBidWinner(state);
            if (!action.Type.StartsWith("trump_")) return;
            string suit = action.Type["trump_".Length..];
            if (!Array.Exists(AllSuits, s => s == suit)) return;

            state.Metadata["bid_trump"] = suit;
            state.Metadata.Remove("bid_winner");

            string player = state.CurrentPlayer == state.Players[0]
                ? "You" : state.CurrentPlayer.Name;
            state.Metadata["status"] = $"{player} named {Capitalize(suit)} trump.";

            state.CurrentPhaseId = _nextPhaseId;
        }

        private void EnsureCurrentPlayerIsBidWinner(GameState state)
        {
            if (!state.Metadata.TryGetValue("bid_winner", out var winnerId)) return;
            int idx = state.Players.FindIndex(p => p.Id == winnerId);
            if (idx >= 0 && state.CurrentPlayerIndex != idx)
                state.CurrentPlayerIndex = idx;
        }

        private string? ResolveExclude(GameState state)
        {
            if (_excludeSuit is null) return null;
            if (_excludeSuit == "bid_excluded_suit")
                return state.Metadata.GetValueOrDefault("bid_excluded_suit");
            return _excludeSuit;
        }

        private static string Capitalize(string s)
            => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
    }

    // ── deal ──────────────────────────────────────────────────────────────────
    // In-round deal phase: burns an optional card then deals N cards to a named
    // zone or to each player's hand.  Auto-advances to the next phase.
    //
    // Phase definition parameters:
    //   to          — zone id | "each_player" (default "community")
    //   count       — cards to deal (default 1)
    //   face        — "up" | "down" (default "down")
    //   burn_first  — true | false (default false)

    private sealed class DealPhaseHandler : IPhaseHandler
    {
        private readonly string _nextPhaseId;
        private readonly string _to;
        private readonly bool   _burnFirst;
        // Simple deal: single count + face.
        private readonly int    _count;
        private readonly bool   _faceUp;
        // Mixed-face deal: list of (count, faceUp) segments from "cards" array.
        private readonly List<(int Count, bool FaceUp)> _segments = [];

        public DealPhaseHandler(PhaseDefinition def, string nextPhaseId)
        {
            _nextPhaseId = nextPhaseId;
            _to          = GetExtra(def, "to") ?? "community";
            _burnFirst   = def.Extra?.TryGetValue("burn_first", out var bfe) == true
                           && bfe.ValueKind == System.Text.Json.JsonValueKind.True;

            // "cards" array overrides simple count/face.
            if (def.Extra?.TryGetValue("cards", out var cardsEl) == true
                && cardsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var seg in cardsEl.EnumerateArray())
                {
                    int  cnt  = seg.TryGetProperty("count", out var cv) && cv.ValueKind == System.Text.Json.JsonValueKind.Number ? cv.GetInt32() : 1;
                    bool face = seg.TryGetProperty("face",  out var fv) && fv.ValueKind == System.Text.Json.JsonValueKind.String
                                && string.Equals(fv.GetString(), "up", StringComparison.OrdinalIgnoreCase);
                    _segments.Add((cnt, face));
                }
                _count = _segments.Sum(s => s.Count);
                _faceUp = false; // unused when _segments is set
            }
            else
            {
                _faceUp = string.Equals(GetExtra(def, "face"), "up", StringComparison.OrdinalIgnoreCase);
                _count  = def.Extra?.TryGetValue("count", out var ce) == true
                          && ce.ValueKind == System.Text.Json.JsonValueKind.Number
                          ? ce.GetInt32() : 1;
            }
        }

        // Auto-advance without user input.
        public TimeSpan? GetAutoAdvanceDelay(GameState _) => TimeSpan.FromMilliseconds(300);
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

        public void Apply(GameState state, GameAction action)
        {
            var deck = state.FindZone("deck");
            if (deck is null || deck.IsEmpty) { state.CurrentPhaseId = _nextPhaseId; return; }

            if (_burnFirst)
            {
                var burn = state.FindZone("burn");
                var burnCard = deck.Draw();
                if (burnCard is not null) burn?.Add(burnCard);
            }

            bool toEachPlayer = string.Equals(_to, "each_player",        StringComparison.OrdinalIgnoreCase)
                             || string.Equals(_to, "each_active_player", StringComparison.OrdinalIgnoreCase);
            bool activeOnly   = string.Equals(_to, "each_active_player", StringComparison.OrdinalIgnoreCase);

            if (toEachPlayer)
            {
                var targets = state.Players
                    .Where(p => !activeOnly || state.Metadata.GetValueOrDefault($"bet_folded:{p.Id}") != "true")
                    .Select(p => state.FindZone($"hand:{p.Id}") ?? state.FindZone("hand"))
                    .ToList();

                if (_segments.Count > 0)
                {
                    // Mixed-face: deal each segment to each player in turn.
                    foreach (var (segCount, segFace) in _segments)
                        foreach (var hand in targets)
                            for (int i = 0; i < segCount && !deck.IsEmpty; i++)
                            { var c = deck.Draw()!; c.IsFaceUp = segFace; hand?.Add(c); }
                }
                else
                {
                    foreach (var hand in targets)
                        for (int i = 0; i < _count && !deck.IsEmpty; i++)
                        { var c = deck.Draw()!; c.IsFaceUp = _faceUp; hand?.Add(c); }
                }
            }
            else
            {
                var target = state.FindZone(_to);
                for (int i = 0; i < _count && !deck.IsEmpty; i++)
                {
                    var card = deck.Draw()!;
                    card.IsFaceUp = _faceUp;
                    target?.Add(card);
                }
            }

            state.CurrentPhaseId = _nextPhaseId;
        }
    }

    // ── score ─────────────────────────────────────────────────────────────────
    // Applies the game's scoring config then auto-advances to the next phase.

    private sealed class ScorePhaseHandler(string nextPhaseId) : IPhaseHandler
    {
        // Brief pause so the player can read the score before the game moves on.
        public TimeSpan? GetAutoAdvanceDelay(GameState _) => TimeSpan.FromMilliseconds(2500);
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("tap")];

        public void Apply(GameState state, GameAction action)
        {
            ScoringEngine.Apply(state);

            // Check win condition immediately after scoring.
            var win = WinConditionEngine.Instance.Check(state);
            if (win is not null)
            {
                state.Metadata["status"]      = win.StatusMessage;
                state.Metadata["last_winner"] = win.WinnerId ?? "";
                state.CurrentPhaseId          = "game_over";
                return;
            }

            state.CurrentPhaseId = nextPhaseId;
        }
    }
}
