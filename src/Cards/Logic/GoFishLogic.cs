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
public sealed class GoFishLogic : GameLogicBase
{
    private int _bookSize = 4;

    // ── Initialize ────────────────────────────────────────────────────────────

    public override void Initialize(GameState state, int playerCount, IReadOnlyList<string> enabledHouseRules)
    {
        _bookSize = enabledHouseRules.Contains("pairs") ? 2 : 4;

        SetupEngine.Instance.Setup(state, playerCount, enabledHouseRules);

        // Standard rule: 7 cards for 2–3 players, 5 for 4+. House rule can override.
        int dealCount = enabledHouseRules.Contains("seven_cards_all")
            ? 7
            : (state.Definition.Deal?.GetCardsPerPlayer(playerCount) ?? (playerCount <= 3 ? 7 : 5));

        StandardDealEngine.Instance.Deal(state, playerCount, enabledHouseRules, dealCount);

        CheckBooks(state, "player0");
        CheckBooks(state, "player1");

        RegisterPhase("player_turn", new PlayerTurnHandler(this));
        RegisterPhase("ai_turn",     new AiTurnHandler(this));

        state.PlayerAgents["player1"] = new GoFishAiAgent("player1");

        state.CurrentPhaseId     = "player_turn";
        state.CurrentPlayerIndex = 0;
        SetIdleStatus(state);
    }

    // ── Phase handlers ────────────────────────────────────────────────────────

    private sealed class PlayerTurnHandler(GoFishLogic logic) : IPhaseHandler
    {
        public IReadOnlyList<GameAction> GetValidActions(GameState state)
        {
            if (state.Zones["hand:player0"].Count == 0)
                return [new GameAction("player_refill")];
            string? sel = state.Metadata.GetValueOrDefault("selected_rank");
            if (sel is null) return [];
            string rankName = GoFishLogic.RankPlural(GoFishLogic.RankFromCode(sel));
            return
            [
                new GameAction("ask",      Label: $"Ask for {rankName}"),
                new GameAction("deselect", Label: "Cancel"),
            ];
        }

        public void Apply(GameState state, GameAction action)
        {
            switch (action.Type)
            {
                case "select_card":   GoFishLogic.SelectCard(state, action.CardId!); break;
                case "ask":           logic.PlayerAsk(state);                         break;
                case "deselect":      GoFishLogic.Deselect(state);                   break;
                case "player_refill": logic.PlayerRefill(state);                      break;
            }
        }

        public TimeSpan? GetAutoAdvanceDelay(GameState state) =>
            state.Zones["hand:player0"].Count == 0 ? TimeSpan.FromMilliseconds(800) : null;

        public IReadOnlyList<string> GetSelectableCardIds(GameState state) =>
            state.Zones["hand:player0"].Cards.Select(c => c.Id).ToList();
    }

    private sealed class AiTurnHandler(GoFishLogic logic) : IPhaseHandler
    {
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [new GameAction("ai_step")];
        public void Apply(GameState state, GameAction action)
        {
            if (action.Type == "ai_step") logic.AiStep(state);
        }
        public TimeSpan? GetAutoAdvanceDelay(GameState _) => TimeSpan.FromMilliseconds(1200);
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
        var aiHand = state.Zones["hand:player1"];
        var deck   = state.Zones["deck"];

        if (aiHand.Count == 0)
        {
            AiHandleEmptyHand(state, deck);
            return;
        }

        string rankCode = AiPickRankCode(state);
        ExecuteAiAsk(state, rankCode);
    }

    private void AiHandleEmptyHand(GameState state, Zone deck)
    {
        var aiHand = state.Zones["hand:player1"];
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
    }

    private static string AiPickRankCode(GameState state)
    {
        if (state.PlayerAgents.TryGetValue("player1", out var agent))
        {
            var masked = GameStateMask.CreateViewFor(state, "player1");
            var action = agent.ChooseAction(masked, [new GameAction("ask_rank")]);
            if (action.Type == "ask_rank" && action.CardId is not null)
                return RankCode(action.CardId);
        }
        return PickAiRankCodeFallback(state);
    }

    private static string PickAiRankCodeFallback(GameState state)
    {
        var aiHand     = state.Zones["hand:player1"];
        var rankGroups = aiHand.Cards
            .GroupBy(c => RankCode(c.Id))
            .OrderByDescending(g => g.Count())
            .ToList();

        var knownPlayerRanks = GetKnownPlayerRanks(state);
        var smartChoice = rankGroups.FirstOrDefault(g => knownPlayerRanks.Contains(g.Key));
        return smartChoice?.Key ?? rankGroups[0].Key;
    }

    private void ExecuteAiAsk(GameState state, string rankCode)
    {
        var aiHand     = state.Zones["hand:player1"];
        var playerHand = state.Zones["hand:player0"];
        var deck       = state.Zones["deck"];
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
            RemoveKnownPlayerRank(state, rankCode);

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

    private static void CheckWinCondition(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return;

        var result = WinConditionEngine.Instance.Check(state);
        if (result is null) return;

        state.Metadata["status"]      = result.StatusMessage;
        state.Metadata["sub"]         = result.SubMessage ?? "";
        state.Metadata["last_winner"] = result.WinnerId   ?? "";
        state.CurrentPhaseId          = "game_over";
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
