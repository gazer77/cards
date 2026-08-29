using System.Text.Json;
using Cards.Logic;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for Go Fish (2+ players, one of which is an AI).
///
/// Phase definition parameters:
///   book_size    — number of matching cards to form a book (default 4).
///   collect_to   — zone-id prefix for book piles (default "books").
///
/// State metadata:
///   gf_state         — "player_turn" | "ai_turn"
///   selected_rank    — rank code the human player tapped ("A","2"…"K")
///   selected_card    — card ID the human player tapped
///   known_p0_ranks   — comma-separated rank codes the AI has observed player0 hold
///   status           — one-line status text
/// </summary>
public sealed class GoFishHandler : IPhaseHandler
{
    private readonly string _nextPhaseId;
    private readonly int    _bookSize;
    private readonly string _collectTo;

    public GoFishHandler(PhaseDefinition def, string nextPhaseId)
    {
        _nextPhaseId = nextPhaseId;
        _bookSize    = GetInt(def, "book_size")    ?? 4;
        _collectTo   = GetString(def, "collect_to") ?? "books";
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public void OnGameStart(GameState state)
    {
        // Register AI agent for player1
        if (state.Players.Count > 1)
            state.PlayerAgents[state.Players[1].Id] = new GoFishAiAgent(state.Players[1].Id);

        // Collect any immediate books from the initial deal
        foreach (var p in state.Players)
            CheckBooks(state, p.Id);

        state.Metadata["gf_state"] = "player_turn";
        SetIdleStatus(state);
    }

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        string gfState = state.Metadata.GetValueOrDefault("gf_state", "player_turn");

        if (gfState == "ai_turn")
            return [new GameAction("ai_step")];

        // Player turn
        var p0Hand = PlayerHand(state, 0);
        if (p0Hand.Count == 0)
            return [new GameAction("player_refill")];

        string? sel = state.Metadata.GetValueOrDefault("selected_rank");
        if (sel is null) return [];

        string rankName = RankPlural(RankFromCode(sel));
        return
        [
            new GameAction("ask",      Label: $"Ask for {rankName}"),
            new GameAction("deselect", Label: "Cancel"),
        ];
    }

    public void Apply(GameState state, GameAction action)
    {
        string gfState = state.Metadata.GetValueOrDefault("gf_state", "player_turn");

        if (gfState == "ai_turn")
        {
            if (action.Type == "ai_step") AiStep(state);
            return;
        }

        // Player turn
        switch (action.Type)
        {
            case "select_card":   SelectCard(state, action.CardId!); break;
            case "ask":           PlayerAsk(state);                  break;
            case "deselect":      Deselect(state);                   break;
            case "player_refill": PlayerRefill(state);               break;
        }
    }

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
    {
        string gfState = state.Metadata.GetValueOrDefault("gf_state", "player_turn");
        if (gfState == "ai_turn") return TimeSpan.FromMilliseconds(1200);
        if (PlayerHand(state, 0).Count == 0) return TimeSpan.FromMilliseconds(800);
        return null;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        if (state.Metadata.GetValueOrDefault("gf_state") == "ai_turn") return [];
        return PlayerHand(state, 0).Cards.Select(c => c.Id).ToList();
    }

    // ── Player actions ────────────────────────────────────────────────────────

    private static void SelectCard(GameState state, string cardId)
    {
        string rankCode = RankCode(cardId);
        state.Metadata["selected_rank"] = rankCode;
        state.Metadata["selected_card"] = cardId;
        state.Metadata["status"]        = $"Ask the AI for {RankPlural(RankFromCode(rankCode))}?";
    }

    private static void Deselect(GameState state)
    {
        state.Metadata.Remove("selected_rank");
        state.Metadata.Remove("selected_card");
        SetIdleStatus(state);
    }

    private void PlayerAsk(GameState state)
    {
        string rankCode = state.Metadata["selected_rank"];
        state.Metadata.Remove("selected_rank");
        state.Metadata.Remove("selected_card");

        var p0Hand  = PlayerHand(state, 0);
        var p1Hand  = PlayerHand(state, 1);
        string rankName = RankPlural(RankFromCode(rankCode));

        RecordKnownPlayerRank(state, rankCode);

        var matching = p1Hand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();

        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                p1Hand.Remove(c);
                c.IsFaceUp = true;
                p0Hand.Add(c);
            }

            int books    = CheckBooks(state, state.Players[0].Id);
            PruneKnownRanks(state);
            string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";
            state.Metadata["status"] = $"Got {matching.Count} {rankName} from the AI!{bookMsg} Go again.";
            CheckWinCondition(state);
        }
        else
        {
            var deck = state.Zones["deck"];
            if (deck.Count > 0)
            {
                var drawn    = deck.Draw()!;
                drawn.IsFaceUp = true;
                p0Hand.Add(drawn);

                // The player has taken one unseen card, so one thing the AI had
                // ruled out may no longer hold.
                ExpireOldestDenial(state);

                int books    = CheckBooks(state, state.Players[0].Id);
                PruneKnownRanks(state);
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string drawnRank = RankPlural(RankFromCode(RankCode(drawn.Id)));
                string bookMsg   = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";

                if (goAgain)
                {
                    RecordKnownPlayerRank(state, rankCode);
                    state.Metadata["status"] = $"Go Fish! Lucky — you drew {drawnRank}.{bookMsg} Go again!";
                }
                else
                {
                    state.Metadata["status"] = $"Go Fish! You drew {drawnRank}.{bookMsg} AI's turn.";
                    EndPlayerTurn(state);
                }
            }
            else
            {
                state.Metadata["status"] = "Go Fish! The deck is empty. AI's turn.";
                EndPlayerTurn(state);
            }

            CheckWinCondition(state);
        }
    }

    private void PlayerRefill(GameState state)
    {
        var p0Hand = PlayerHand(state, 0);
        var deck   = state.Zones["deck"];

        if (deck.Count > 0)
        {
            var drawn = deck.Draw()!;
            drawn.IsFaceUp = true;
            p0Hand.Add(drawn);
            ExpireOldestDenial(state);
            CheckBooks(state, state.Players[0].Id);
            PruneKnownRanks(state);
            if (p0Hand.Count > 0)
                state.Metadata["status"] = "You had no cards — drew one from the deck.";
        }
        else
        {
            state.Metadata["status"] = "You have no cards and the deck is empty. AI's turn.";
            EndPlayerTurn(state);
        }

        CheckWinCondition(state);
    }

    private static void EndPlayerTurn(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;
        state.Metadata["gf_state"]   = "ai_turn";
        state.CurrentPlayerIndex = 1;
    }

    // ── AI turn ───────────────────────────────────────────────────────────────

    private void AiStep(GameState state)
    {
        var aiId   = state.Players[1].Id;
        var aiHand = PlayerHand(state, 1);
        var deck   = state.Zones["deck"];

        if (aiHand.Count == 0)
        {
            if (deck.Count > 0)
            {
                var c = deck.Draw()!;
                c.IsFaceUp = false;
                aiHand.Add(c);
                CheckBooks(state, aiId);
                state.Metadata["status"] = "AI has no cards — drew from deck. Your turn!";
            }
            else
            {
                state.Metadata["status"] = "AI has no cards and the deck is empty. Your turn!";
            }
            EndAiTurn(state);
            CheckWinCondition(state);
            return;
        }

        string rankCode = PickAiRankCode(state);
        ExecuteAiAsk(state, rankCode);
    }

    private static string PickAiRankCode(GameState state)
    {
        var aiId = state.Players[1].Id;
        if (state.PlayerAgents.TryGetValue(aiId, out var agent))
        {
            var masked = GameStateMask.CreateViewFor(state, aiId);
            var action = agent.ChooseAction(masked, [new GameAction("ask_rank")]);
            if (action.Type == "ask_rank" && action.CardId is not null)
                return RankCode(action.CardId);
        }

        // Fallback heuristic: pick rank held most, preferring known player ranks and
        // avoiding ones already refused.
        var aiHand     = state.Zones[$"hand:{aiId}"];
        var rankGroups = aiHand.Cards
            .GroupBy(c => RankCode(c.Id))
            .OrderByDescending(g => g.Count())
            .ToList();

        var knownRanks = GetKnownPlayerRanks(state);
        var denied     = GetDeniedRanks(state);

        return rankGroups.FirstOrDefault(g => knownRanks.Contains(g.Key))?.Key
            ?? rankGroups.FirstOrDefault(g => !denied.Contains(g.Key))?.Key
            // Everything the AI holds has been refused. Asking anyway is a wasted turn
            // either way, so fall back rather than fail.
            ?? rankGroups[0].Key;
    }

    private void ExecuteAiAsk(GameState state, string rankCode)
    {
        var aiId   = state.Players[1].Id;
        var aiHand = PlayerHand(state, 1);
        var p0Hand = PlayerHand(state, 0);
        var deck   = state.Zones["deck"];
        string rankName = RankPlural(RankFromCode(rankCode));

        var matching = p0Hand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();

        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                p0Hand.Remove(c);
                c.IsFaceUp = false;
                aiHand.Add(c);
            }

            if (!p0Hand.Cards.Any(c => RankCode(c.Id) == rankCode))
            {
                RemoveKnownPlayerRank(state, rankCode);

                // A successful ask takes every card of that rank, so the player is now
                // known to hold none — asking again would be the same wasted turn.
                RecordDeniedRank(state, rankCode);
            }

            int books    = CheckBooks(state, aiId);
            string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";
            state.Metadata["status"] = $"AI asked for {rankName} — got {matching.Count}!{bookMsg} AI goes again…";
        }
        else
        {
            RemoveKnownPlayerRank(state, rankCode);
            RecordDeniedRank(state, rankCode);

            if (deck.Count > 0)
            {
                var drawn = deck.Draw()!;
                drawn.IsFaceUp = false;
                aiHand.Add(drawn);

                int books    = CheckBooks(state, aiId);
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";

                if (goAgain)
                    state.Metadata["status"] = $"AI asked for {rankName} — Go Fish, but drew one!{bookMsg} AI goes again…";
                else
                {
                    state.Metadata["status"] = $"AI asked for {rankName} — Go Fish.{bookMsg} Your turn!";
                    EndAiTurn(state);
                }
            }
            else
            {
                state.Metadata["status"] = $"AI asked for {rankName} — Go Fish! Deck is empty. Your turn!";
                EndAiTurn(state);
            }
        }

        CheckWinCondition(state);
    }

    private static void EndAiTurn(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;
        state.Metadata["gf_state"]   = "player_turn";
        state.CurrentPlayerIndex = 0;
    }

    // ── Book detection ────────────────────────────────────────────────────────

    private int CheckBooks(GameState state, string playerId)
    {
        var hand  = state.Zones[$"hand:{playerId}"];
        var books = state.FindZone($"{_collectTo}:{playerId}");

        var groups = hand.Cards
            .GroupBy(c => RankCode(c.Id))
            .Where(g => g.Count() >= _bookSize)
            .ToList();

        foreach (var group in groups)
        {
            var toMove = group.Take(_bookSize).ToList();
            foreach (var c in toMove)
            {
                hand.Remove(c);
                c.IsFaceUp = true;
                books?.Add(c);
            }
            state.AddScore(playerId, 1);
        }

        return groups.Count;
    }

    // ── Win condition ─────────────────────────────────────────────────────────

    private static void CheckWinCondition(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;

        var result = WinConditionEngine.Instance.Check(state);
        if (result is null) return;

        state.Metadata["status"]      = result.StatusMessage;
        state.Metadata["sub"]         = result.SubMessage ?? "";
        state.Metadata["last_winner"] = result.WinnerId ?? "";
        state.CurrentPhaseId          = "game_over";
    }

    // ── AI memory ─────────────────────────────────────────────────────────────

    private static HashSet<string> GetKnownPlayerRanks(GameState state)
    {
        var val = state.Metadata.GetValueOrDefault("known_p0_ranks", "");
        return string.IsNullOrEmpty(val) ? [] : [.. val.Split(',')];
    }

    private static void RecordKnownPlayerRank(GameState state, string rankCode)
    {
        var known = GetKnownPlayerRanks(state);
        known.Add(rankCode);
        state.Metadata["known_p0_ranks"] = string.Join(',', known);

        // Seeing the player hold a rank is direct evidence, and outranks having been
        // told at some earlier point that they had none.
        var denied = GetDeniedRanks(state);
        if (denied.Remove(rankCode))
        {
            if (denied.Count == 0) state.Metadata.Remove(DeniedKey);
            else                   state.Metadata[DeniedKey] = string.Join(',', denied);
        }
    }

    private static void RemoveKnownPlayerRank(GameState state, string rankCode)
    {
        var known = GetKnownPlayerRanks(state);
        if (known.Remove(rankCode))
            state.Metadata["known_p0_ranks"] = string.Join(',', known);
    }

    // ── Denied ranks ──────────────────────────────────────────────────────────
    //
    // Asking for a rank the opponent has already refused is legal but is not how the
    // game is played: it cannot succeed, and every failure draws a card, so an AI with
    // no memory of refusals empties the deck asking the same question over and over.
    // Its fallback pick — "the rank I hold most of" — is deterministic, so without this
    // it does exactly that.

    private const string DeniedKey = "gf_denied_p0";

    internal static HashSet<string> GetDeniedRanks(GameState state)
    {
        var val = state.Metadata.GetValueOrDefault(DeniedKey, "");
        return string.IsNullOrEmpty(val) ? [] : [.. val.Split(',')];
    }

    private static void RecordDeniedRank(GameState state, string rankCode)
    {
        var denied = GetDeniedRanks(state);
        denied.Add(rankCode);
        state.Metadata[DeniedKey] = string.Join(',', denied);
    }

    /// <summary>
    /// Retires the oldest refusal, because the player has taken one unseen card.
    ///
    /// One card can restore at most one ruled-out rank, so forgetting everything on
    /// each draw is far too much — it sends the AI straight back to the question it
    /// just had refused, which is the behaviour this whole mechanism exists to stop.
    /// Forgetting nothing is the opposite mistake: a rank the player draws would be
    /// ruled out permanently. Expiring them oldest-first tracks the real uncertainty
    /// without either failure.
    /// </summary>
    private static void ExpireOldestDenial(GameState state)
    {
        var val = state.Metadata.GetValueOrDefault(DeniedKey, "");
        if (string.IsNullOrEmpty(val)) return;

        var denied = val.Split(',').ToList();
        denied.RemoveAt(0);

        if (denied.Count == 0) state.Metadata.Remove(DeniedKey);
        else                   state.Metadata[DeniedKey] = string.Join(',', denied);
    }

    private static void PruneKnownRanks(GameState state)
    {
        var known = GetKnownPlayerRanks(state);
        if (known.Count == 0) return;
        var actual = PlayerHand(state, 0).Cards.Select(c => RankCode(c.Id)).ToHashSet();
        known.IntersectWith(actual);
        state.Metadata["known_p0_ranks"] = string.Join(',', known);
    }

    // ── Zone helpers ──────────────────────────────────────────────────────────

    private static Zone PlayerHand(GameState state, int playerIndex)
        => state.Zones[$"hand:{state.Players[playerIndex].Id}"];

    // ── Status ────────────────────────────────────────────────────────────────

    private static void SetIdleStatus(GameState state)
    {
        int p0 = state.GetScore(state.Players[0].Id);
        int p1 = state.Players.Count > 1 ? state.GetScore(state.Players[1].Id) : 0;
        string books = (p0 > 0 || p1 > 0) ? $"  (Books — You: {p0} | AI: {p1})" : "";
        state.Metadata["status"] = $"Tap a card to ask for its rank.{books}";
    }

    // ── Rank helpers ──────────────────────────────────────────────────────────

    private static string RankCode(string cardId) => cardId[..^1];

    private static Rank RankFromCode(string code) => code switch
    {
        "A" => Rank.Ace,
        "J" => Rank.Jack,
        "Q" => Rank.Queen,
        "K" => Rank.King,
        _   => (Rank)int.Parse(code),
    };

    private static string RankPlural(Rank rank) => rank switch
    {
        Rank.Ace   => "Aces",
        Rank.Two   => "Twos",
        Rank.Three => "Threes",
        Rank.Four  => "Fours",
        Rank.Five  => "Fives",
        Rank.Six   => "Sixes",
        Rank.Seven => "Sevens",
        Rank.Eight => "Eights",
        Rank.Nine  => "Nines",
        Rank.Ten   => "Tens",
        Rank.Jack  => "Jacks",
        Rank.Queen => "Queens",
        Rank.King  => "Kings",
        _          => rank.ToString() + "s",
    };

    // ── JSON helpers ──────────────────────────────────────────────────────────

    private static string? GetString(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.String)
            return el.GetString();
        return null;
    }

    private static int? GetInt(PhaseDefinition def, string key)
    {
        if (def.Extra?.TryGetValue(key, out var el) == true && el.ValueKind == JsonValueKind.Number)
            return el.GetInt32();
        return null;
    }
}
