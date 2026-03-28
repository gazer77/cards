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
///   selected_rank — rank code of the card the player tapped ("A","2"…"K")
///   selected_card — card ID the player tapped (drives selection highlight)
///   status        — one-line status text shown on screen
///   sub           — subtitle shown on the game-over overlay
/// </summary>
public sealed class GoFishLogic : IGameLogic
{
    private int _bookSize = 4;

    // ── Initialize ────────────────────────────────────────────────────────────

    public void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _bookSize = enabledHouseRules.Contains("pairs") ? 2 : 4;
        bool sevenCards = enabledHouseRules.Contains("seven_cards_two_players");

        state.Players.Add(new Player("player0", "You"));
        state.Players.Add(new Player("player1", "AI"));

        state.Zones["deck"]         = new Zone("deck",         "deck", null,      "none");
        state.Zones["hand:player0"] = new Zone("hand:player0", "hand", "player0", "owner");
        state.Zones["hand:player1"] = new Zone("hand:player1", "hand", "player1", "none");

        var deck = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(deck);
        foreach (var c in deck) state.Zones["deck"].Add(c);

        int dealCount = sevenCards ? 7 : 5;
        DealCards(state, "player0", dealCount, faceUp: true);
        DealCards(state, "player1", dealCount, faceUp: false);

        CheckBooks(state, "player0");
        CheckBooks(state, "player1");

        state.CurrentPhaseId     = "player_turn";
        state.CurrentPlayerIndex = 0;
        UpdatePlayerTurnStatus(state);
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return [];
        if (state.CurrentPhaseId == "ai_turn")   return [new GameAction("tap")];

        // player_turn
        string? sel = state.Metadata.GetValueOrDefault("selected_rank");
        if (sel is null) return []; // waiting for card tap (select_card)

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
                    case "select_card": SelectCard(state, action.CardId!); break;
                    case "ask":         PlayerAsk(state);                   break;
                    case "deselect":    Deselect(state);                    break;
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
        UpdatePlayerTurnStatus(state);
    }

    private void PlayerAsk(GameState state)
    {
        string rankCode = state.Metadata["selected_rank"];
        state.Metadata.Remove("selected_rank");
        state.Metadata.Remove("selected_card");

        var playerHand = state.Zones["hand:player0"];
        var aiHand     = state.Zones["hand:player1"];
        string rankName = RankPlural(RankFromCode(rankCode));

        var matching = aiHand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();

        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                aiHand.Remove(c);
                c.IsFaceUp = true;
                playerHand.Add(c);
            }

            int books = CheckBooks(state, "player0");
            string bookMsg = books > 0 ? " Book complete!" : "";
            state.Metadata["status"] = $"Got {matching.Count} {rankName}!{bookMsg} Go again.";
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
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string drawnRank = RankPlural(RankFromCode(RankCode(drawn.Id)));
                string bookMsg = books > 0 ? " Book complete!" : "";

                if (goAgain)
                {
                    state.Metadata["status"] = $"Go Fish! Drew {drawnRank}.{bookMsg} Go again!";
                    // Stay in player_turn
                }
                else
                {
                    state.Metadata["status"] = $"Go Fish! Drew {drawnRank}.{bookMsg}";
                    EndPlayerTurn(state);
                }
            }
            else
            {
                state.Metadata["status"] = "Go Fish! The deck is empty.";
                EndPlayerTurn(state);
            }

            CheckWinCondition(state);
        }
    }

    private static void EndPlayerTurn(GameState state)
    {
        state.CurrentPhaseId     = "ai_turn";
        state.CurrentPlayerIndex = 1;
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
                state.Metadata["status"] = "AI has no cards — drew from deck.";
            }
            else
            {
                state.Metadata["status"] = "AI has no cards and deck is empty.";
            }
            EndAiTurn(state);
            CheckWinCondition(state);
            return;
        }

        // Pick the rank the AI holds the most of
        string rankCode = aiHand.Cards
            .GroupBy(c => RankCode(c.Id))
            .OrderByDescending(g => g.Count())
            .First().Key;

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

            int books    = CheckBooks(state, "player1");
            string bookMsg = books > 0 ? " Book complete!" : "";
            state.Metadata["status"] = $"AI asked for {rankName} — got {matching.Count}!{bookMsg} AI goes again.";
            // Stay in ai_turn
        }
        else
        {
            // Go Fish
            if (deck.Count > 0)
            {
                var drawn = deck.Draw()!;
                drawn.IsFaceUp = false;
                aiHand.Add(drawn);

                int books    = CheckBooks(state, "player1");
                bool goAgain = RankCode(drawn.Id) == rankCode;
                string bookMsg = books > 0 ? " Book complete!" : "";

                if (goAgain)
                {
                    state.Metadata["status"] = $"AI asked for {rankName} — Go Fish, got one!{bookMsg} AI goes again.";
                    // Stay in ai_turn
                }
                else
                {
                    state.Metadata["status"] = $"AI asked for {rankName} — Go Fish.{bookMsg}";
                    EndAiTurn(state);
                }
            }
            else
            {
                state.Metadata["status"] = $"AI asked for {rankName} — Go Fish! Deck is empty.";
                EndAiTurn(state);
            }
        }

        CheckWinCondition(state);
    }

    private static void EndAiTurn(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;
        state.CurrentPhaseId     = "player_turn";
        state.CurrentPlayerIndex = 0;
        UpdatePlayerTurnStatus(state);
    }

    // ── Book detection ────────────────────────────────────────────────────────

    private int CheckBooks(GameState state, string playerId)
    {
        var hand    = state.Zones[$"hand:{playerId}"];
        var groups  = hand.Cards
            .GroupBy(c => RankCode(c.Id))
            .Where(g => g.Count() >= _bookSize)
            .ToList();

        foreach (var group in groups)
        {
            var toRemove = group.Take(_bookSize).ToList();
            foreach (var c in toRemove)
                hand.Remove(c);
            state.AddScore(playerId, 1);
        }

        return groups.Count;
    }

    // ── Win condition ─────────────────────────────────────────────────────────

    private void CheckWinCondition(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;

        // All books formed: deck and both hands empty
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

    public TimeSpan? GetAutoAdvanceDelay(GameState state) =>
        state.CurrentPhaseId == "ai_turn" ? TimeSpan.FromMilliseconds(900) : null;

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        if (state.CurrentPhaseId != "player_turn") return [];

        var hand = state.Zones["hand:player0"];
        string? selRank = state.Metadata.GetValueOrDefault("selected_rank");

        // After rank selection: highlight only cards of that rank
        if (selRank is not null)
            return hand.Cards.Where(c => RankCode(c.Id) == selRank).Select(c => c.Id).ToList();

        // Before selection: all cards are tappable
        return hand.Cards.Select(c => c.Id).ToList();
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

    private static void UpdatePlayerTurnStatus(GameState state)
    {
        int p0 = state.GetScore("player0");
        int p1 = state.GetScore("player1");
        string books = (p0 > 0 || p1 > 0) ? $"  (Books — You: {p0}, AI: {p1})" : "";
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
