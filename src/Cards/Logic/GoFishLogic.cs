using Cards.Engine;

namespace Cards.Logic;

/// <summary>
/// Implements Go Fish for 2 players (human vs AI).
///
/// Zones:
///   deck          — draw pile (face-down)
///   hand:player0  — human's hand (face-up, visibility: owner)
///   hand:player1  — AI's hand   (face-down, visibility: none)
///
/// Phases:
///   player_turn   — human taps a card → select_card; then ask or deselect
///   ai_turn       — auto-advances every 900 ms; AI asks and draws automatically
///   game_over     — triggered when deck + both hands are empty
///
/// Metadata keys:
///   selected_rank    — rank code of the card the player tapped ("A","2"…"K")
///   selected_card    — card ID the player tapped (drives selection highlight)
///   status           — one-line status text shown on screen
///   sub              — subtitle shown on the game-over overlay
///   known_p0_ranks   — comma-separated rank codes the AI knows the player holds
///                      (updated each time the player asks for a rank)
/// </summary>
public sealed class GoFishLogic : IGameLogic
{
    private int _bookSize = 4;

    // ── Initialize ────────────────────────────────────────────────────────────

    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _bookSize = enabledHouseRules.Contains("pairs") ? 2 : 4;

        state.Players.Add(new Player("player0", "You"));
        state.Players.Add(new Player("player1", "AI"));

        state.Zones["deck"]           = new Zone("deck",           "deck",   null,      "none");
        state.Zones["hand:player0"]   = new Zone("hand:player0",   "hand",   "player0", "owner");
        state.Zones["hand:player1"]   = new Zone("hand:player1",   "hand",   "player1", "none");
        state.Zones["books:player0"]  = new Zone("books:player0",  "spread", "player0", "all");
        state.Zones["books:player1"]  = new Zone("books:player1",  "spread", "player1", "all");

        var deck = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(deck);
        foreach (var c in deck) state.Zones["deck"].Add(c);

        // Standard rule: 7 cards for 2-3 players, 5 for 4+. House rule can override.
        int dealCount = enabledHouseRules.Contains("seven_cards_all")
            ? 7
            : (state.Definition.Deal?.GetCardsPerPlayer(playerCount) ?? (playerCount <= 3 ? 7 : 5));
        DealCards(state, "player0", dealCount, faceUp: true);
        DealCards(state, "player1", dealCount, faceUp: false);

        CheckBooks(state, "player0");
        CheckBooks(state, "player1");

        state.CurrentPhaseId     = "player_turn";
        state.CurrentPlayerIndex = 0;
        SetIdleStatus(state);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return [];
        if (state.CurrentPhaseId == "ai_turn")   return [new GameAction("ai_step")];

        // Player has no cards — auto-draw or pass
        if (state.Zones["hand:player0"].Count == 0)
            return [new GameAction("player_refill")];

        // player_turn: waiting for card tap
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
        switch (state.CurrentPhaseId)
        {
            case "player_turn":
                switch (action.Type)
                {
                    case "select_card":   SelectCard(state, action.CardId!); break;
                    case "ask":           PlayerAsk(state);                   break;
                    case "deselect":      Deselect(state);                    break;
                    case "player_refill": PlayerRefill(state);                break;
                }
                break;

            case "ai_turn":
                AiStep(state);
                break;
        }
    }

    // ── Player actions ────────────────────────────────────────────────────────

    private static void SelectCard(GameState state, string cardId)
    {
        string rankCode = RankCode(cardId);
        state.Metadata["selected_rank"] = rankCode;
        state.Metadata["selected_card"] = cardId;
        state.Metadata["status"] = $"Ask the AI for {RankPlural(RankFromCode(rankCode))}?";
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

        var playerHand = state.Zones["hand:player0"];
        var aiHand     = state.Zones["hand:player1"];
        string rankName = RankPlural(RankFromCode(rankCode));

        // The AI now knows the player has (or had) this rank
        RecordKnownPlayerRank(state, rankCode);

        var matching = aiHand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();

        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                aiHand.Remove(c);
                c.IsFaceUp = true;
                playerHand.Add(c);
            }

            int books    = CheckBooks(state, "player0");
            PruneKnownRanks(state); // player may have completed their book
            string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";
            state.Metadata["status"] =
                $"Got {matching.Count} {rankName} from the AI!{bookMsg} Go again.";
            // Stay in player_turn (go again)
            CheckWinCondition(state);
        }
        else
        {
            // Go Fish
            var deck = state.Zones["deck"];
            if (deck.Count > 0)
            {
                var drawn = deck.Draw()!;
                drawn.IsFaceUp = true;
                playerHand.Add(drawn);

                int books    = CheckBooks(state, "player0");
                PruneKnownRanks(state);
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string drawnRank = RankPlural(RankFromCode(RankCode(drawn.Id)));
                string bookMsg   = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";

                if (goAgain)
                {
                    // Lucky draw — player drew what they asked for
                    RecordKnownPlayerRank(state, rankCode); // still has it
                    state.Metadata["status"] =
                        $"Go Fish! Lucky — you drew {drawnRank}.{bookMsg} Go again!";
                    // Stay in player_turn
                }
                else
                {
                    state.Metadata["status"] =
                        $"Go Fish! You drew {drawnRank}.{bookMsg} AI's turn.";
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

    private static void EndPlayerTurn(GameState state)
    {
        state.CurrentPhaseId     = "ai_turn";
        state.CurrentPlayerIndex = 1;
        // Don't update status here — keep the player's result message visible
        // during the 900 ms delay before AiStep runs.
    }

    // ── AI turn ───────────────────────────────────────────────────────────────

    private void AiStep(GameState state)
    {
        var aiHand     = state.Zones["hand:player1"];
        var playerHand = state.Zones["hand:player0"];
        var deck       = state.Zones["deck"];

        // If AI has no cards, draw one if possible then end turn
        if (aiHand.Count == 0)
        {
            if (deck.Count > 0)
            {
                var c = deck.Draw()!;
                c.IsFaceUp = false;
                aiHand.Add(c);
                CheckBooks(state, "player1");
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

        // ── Strategy: prefer ranks the AI holds AND knows the player has ──────
        var aiRankGroups = aiHand.Cards
            .GroupBy(c => RankCode(c.Id))
            .OrderByDescending(g => g.Count())
            .ToList();

        var knownPlayerRanks = GetKnownPlayerRanks(state);

        // First priority: a rank we hold ≥1 of AND we know the player has
        var smartChoice = aiRankGroups
            .FirstOrDefault(g => knownPlayerRanks.Contains(g.Key));

        // Fallback: rank we hold the most of (greedy)
        string rankCode = smartChoice?.Key ?? aiRankGroups[0].Key;
        string rankName = RankPlural(RankFromCode(rankCode));

        var matching = playerHand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();

        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                playerHand.Remove(c);
                c.IsFaceUp = false;
                aiHand.Add(c);
            }

            // If player now has 0 of that rank, remove it from known
            if (!playerHand.Cards.Any(c => RankCode(c.Id) == rankCode))
                RemoveKnownPlayerRank(state, rankCode);

            int books    = CheckBooks(state, "player1");
            string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";

            state.Metadata["status"] =
                $"AI asked for {rankName} — got {matching.Count}!{bookMsg} AI goes again…";
            // Stay in ai_turn (go again)
        }
        else
        {
            // The player didn't have them — clear this from known ranks if present
            RemoveKnownPlayerRank(state, rankCode);

            // Go Fish
            if (deck.Count > 0)
            {
                var drawn = deck.Draw()!;
                drawn.IsFaceUp = false;
                aiHand.Add(drawn);

                int books    = CheckBooks(state, "player1");
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string bookMsg = books > 0 ? $" {books} book{(books > 1 ? "s" : "")} complete!" : "";

                if (goAgain)
                {
                    state.Metadata["status"] =
                        $"AI asked for {rankName} — Go Fish, but drew one!{bookMsg} AI goes again…";
                    // Stay in ai_turn
                }
                else
                {
                    state.Metadata["status"] =
                        $"AI asked for {rankName} — Go Fish.{bookMsg} Your turn!";
                    EndAiTurn(state);
                }
            }
            else
            {
                state.Metadata["status"] =
                    $"AI asked for {rankName} — Go Fish! Deck is empty. Your turn!";
                EndAiTurn(state);
            }
        }

        CheckWinCondition(state);
    }

    private void PlayerRefill(GameState state)
    {
        var hand = state.Zones["hand:player0"];
        var deck = state.Zones["deck"];

        if (deck.Count > 0)
        {
            var drawn = deck.Draw()!;
            drawn.IsFaceUp = true;
            hand.Add(drawn);
            CheckBooks(state, "player0");
            PruneKnownRanks(state);
            if (hand.Count > 0)
                state.Metadata["status"] = "You had no cards — drew one from the deck.";
            // If CheckBooks immediately emptied the hand again the loop will refill again
        }
        else
        {
            state.Metadata["status"] = "You have no cards and the deck is empty. AI's turn.";
            EndPlayerTurn(state);
        }
        CheckWinCondition(state);
    }

    private static void EndAiTurn(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;
        state.CurrentPhaseId     = "player_turn";
        state.CurrentPlayerIndex = 0;
        // Don't reset status here — keep the AI's last action message visible
        // until the player taps a card (SelectCard will then update it).
    }

    // ── Book detection ────────────────────────────────────────────────────────

    private int CheckBooks(GameState state, string playerId)
    {
        var hand  = state.Zones[$"hand:{playerId}"];
        var books = state.FindZone($"books:{playerId}");

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

    private void CheckWinCondition(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;

        var p0Hand = state.Zones["hand:player0"];
        var p1Hand = state.Zones["hand:player1"];
        var deck   = state.Zones["deck"];

        if (deck.Count > 0 || p0Hand.Count > 0 || p1Hand.Count > 0) return;

        int p0Books = state.GetScore("player0");
        int p1Books = state.GetScore("player1");

        string result = p0Books > p1Books ? "You win!"
            : p1Books > p0Books           ? "AI wins."
            :                               "It's a tie!";

        state.Metadata["status"] = result;
        state.Metadata["sub"]    = $"Your books: {p0Books} | AI books: {p1Books}";
        state.CurrentPhaseId     = "game_over";
    }

    // ── IGameLogic ────────────────────────────────────────────────────────────

    public bool IsGameOver(GameState state) => state.CurrentPhaseId == "game_over";

    public string GetStatusText(GameState state)
        => state.Metadata.GetValueOrDefault("status", "");

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
    {
        if (state.CurrentPhaseId == "ai_turn") return TimeSpan.FromMilliseconds(1200);
        // Player has no cards — auto-handle after a brief pause
        if (state.CurrentPhaseId == "player_turn" && state.Zones["hand:player0"].Count == 0)
            return TimeSpan.FromMilliseconds(800);
        return null;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        if (state.CurrentPhaseId != "player_turn") return [];

        // All hand cards are always tappable — tapping a card selects or re-selects it
        return state.Zones["hand:player0"].Cards.Select(c => c.Id).ToList();
    }

    // ── AI memory helpers ─────────────────────────────────────────────────────

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
    }

    private static void RemoveKnownPlayerRank(GameState state, string rankCode)
    {
        var known = GetKnownPlayerRanks(state);
        if (known.Remove(rankCode))
            state.Metadata["known_p0_ranks"] = string.Join(',', known);
    }

    /// <summary>
    /// Removes any ranks from the AI's known-player-ranks that the player
    /// no longer holds (e.g., after completing a book or transferring cards).
    /// </summary>
    private static void PruneKnownRanks(GameState state)
    {
        var known = GetKnownPlayerRanks(state);
        if (known.Count == 0) return;
        var actual = state.Zones["hand:player0"].Cards
            .Select(c => RankCode(c.Id)).ToHashSet();
        known.IntersectWith(actual);
        state.Metadata["known_p0_ranks"] = string.Join(',', known);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void DealCards(GameState state, string playerId, int count, bool faceUp)
    {
        var deck = state.Zones["deck"];
        var hand = state.Zones[$"hand:{playerId}"];
        for (int i = 0; i < count && deck.Count > 0; i++)
        {
            var card = deck.Draw()!;
            card.IsFaceUp = faceUp;
            hand.Add(card);
        }
    }

    /// <summary>Sets the idle prompt shown at the start of the player's turn.</summary>
    private static void SetIdleStatus(GameState state)
    {
        int p0 = state.GetScore("player0");
        int p1 = state.GetScore("player1");
        string books = (p0 > 0 || p1 > 0) ? $"  (Books — You: {p0} | AI: {p1})" : "";
        state.Metadata["status"] = $"Tap a card to ask for its rank.{books}";
    }

    /// <summary>Extracts the rank code from a card ID, e.g. "Kh" → "K", "10s" → "10".</summary>
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
}
