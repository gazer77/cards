using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Heuristic AI agent that replaces the purely random DefaultAiAgent.
///
/// Strategy by action context:
///   play_card in trick-taking — lowest-beater-or-dump with lead/trump awareness.
///       For Hearts, actively avoids capturing point cards.
///   draw_from_*              — prefers drawing from discard over deck when visible
///       discard card ranks below the average hand rank.
///   Poker betting            — conservative: call if pot odds reasonable, fold high.
///   Everything else          — random (fall-through).
/// </summary>
public sealed class SmartDefaultAiAgent : IPlayerAgent
{
    private readonly Random _rng = new();

    public string PlayerId { get; }

    public SmartDefaultAiAgent(string playerId) => PlayerId = playerId;

    // ── IPlayerAgent ──────────────────────────────────────────────────────────

    public GameAction ChooseAction(GameState state, IReadOnlyList<GameAction> validActions)
    {
        if (validActions.Count == 0) return new GameAction("tap");
        if (validActions.Count == 1) return validActions[0];

        // Gin Rummy: always gin immediately; knock when available (it's +EV to stop)
        if (validActions.Any(a => a.Type == "gin"))
            return validActions.First(a => a.Type == "gin");
        if (validActions.Any(a => a.Type == "knock"))
            return validActions.First(a => a.Type == "knock");

        // Card-play decisions — detect context before generic trick-taking
        var plays = validActions.Where(a => a.Type == "play_card" && a.CardId is not null).ToList();
        if (plays.Count > 0)
        {
            // Pass-cards phase (Hearts): pick high-value cards not already selected.
            if (state.Metadata.ContainsKey("pass_direction"))
                return ChoosePassCard(state, plays);

            // Golf grid-swap: drawn card is one of the play_card options
            string? drawnCardId = state.Metadata.GetValueOrDefault("dd_drawn_card");
            if (drawnCardId is not null && plays.Any(a => a.CardId == drawnCardId))
                return ChooseGolfGridAction(state, plays, drawnCardId);

            // Draw-discard discard phase (gin rummy / hand-and-foot / etc.): smarter discard
            if (state.Metadata.GetValueOrDefault("dd_turn_state") == "discard")
                return ChooseDiscardCard(state, plays);

            return ChooseTrickCard(state, plays);
        }

        // Draw phase for draw-discard games
        if (validActions.Any(a => a.Type.StartsWith("draw_from_")))
            return ChooseDraw(state, validActions);

        // Poker betting — simple conservative strategy
        if (validActions.Any(a => a.Type is "call" or "check" or "fold" or "raise"))
            return ChoosePokerAction(state, validActions);

        // Bidding — number, accept/pass, suit/pass styles
        if (validActions.Any(a => a.Type.StartsWith("bid_")))
            return ChooseBid(state, validActions);

        // Trump naming (name_trump phase): pick suit held most
        if (validActions.Any(a => a.Type.StartsWith("trump_")))
            return ChooseTrump(state, validActions);

        // meld_done / lay_meld — prefer laying melds before ending
        if (validActions.Any(a => a.Type == "lay_meld"))
            return validActions.First(a => a.Type == "lay_meld");

        // Default: random
        return validActions[_rng.Next(validActions.Count)];
    }

    // ── Trick-taking strategy ─────────────────────────────────────────────────

    private GameAction ChooseTrickCard(GameState state, List<GameAction> plays)
    {
        var myCards = GetCardsByIds(state, plays.Select(a => a.CardId!));
        if (myCards.Count == 0) return plays[_rng.Next(plays.Count)];

        Suit? trump    = GetTrump(state);
        bool isHearts  = state.Metadata.ContainsKey("trick_hearts_broken")
                         || state.GameId == "hearts";
        var trickCards = GetTrickCards(state);
        bool leading   = trickCards.Count == 0;

        Card chosen = leading
            ? ChooseLeadCard(myCards, trump, isHearts)
            : ChooseFollowCard(myCards, trickCards, trump, isHearts);

        return plays.First(a => a.CardId == chosen.Id);
    }

    private Card ChooseLeadCard(List<Card> hand, Suit? trump, bool avoidPoints)
    {
        if (avoidPoints)
        {
            // Hearts: lead lowest non-point, non-trump card
            var safe = hand.Where(c => !IsPointCard(c) && (trump is null || c.Suit != trump))
                          .OrderBy(c => (int)c.Rank).ToList();
            if (safe.Count > 0) return safe[0];
            // Fall back to lowest
            return hand.OrderBy(c => (int)c.Rank).First();
        }

        // Aggressive: lead highest card in non-trump suit
        var nonTrump = hand.Where(c => trump is null || c.Suit != trump)
                          .OrderByDescending(c => (int)c.Rank).ToList();
        if (nonTrump.Count > 0) return nonTrump[0];
        return hand.OrderByDescending(c => (int)c.Rank).First();
    }

    private Card ChooseFollowCard(List<Card> hand, List<Card> trickCards, Suit? trump, bool avoidPoints)
    {
        var winner = FindTrickWinner(trickCards, trump);

        if (avoidPoints)
        {
            // Hearts: dump the highest-value point card if can't avoid winning, else play lowest
            var beaters = winner is null ? [] : hand.Where(c => CanBeat(c, winner, trump)).ToList();
            if (beaters.Count == 0 || IsWinnerPointFree(trickCards))
            {
                // Either can't win, or trick has no points — dump lowest card
                return hand.OrderBy(c => PointValue(c)).ThenBy(c => (int)c.Rank).First();
            }
            // Trick has points and we might be forced to win — dump points if we can't avoid it
            // Actually prefer to NOT win: play lowest card that loses
            var losers = winner is null ? hand : hand.Where(c => !CanBeat(c, winner, trump)).ToList();
            if (losers.Count > 0)
                return losers.OrderBy(c => (int)c.Rank).First();
            // Forced to win: dump highest point card to at least "use" it
            return hand.OrderByDescending(c => PointValue(c)).ThenByDescending(c => (int)c.Rank).First();
        }

        // Standard trick-taking: win with lowest beater, else dump lowest
        if (winner is not null)
        {
            var beaters = hand.Where(c => CanBeat(c, winner, trump))
                              .OrderBy(c => (int)c.Rank).ToList();
            if (beaters.Count > 0) return beaters[0];
        }

        // Can't beat (or leading): dump lowest card
        return hand.OrderBy(c => (int)c.Rank).First();
    }

    // ── Golf grid-swap strategy ───────────────────────────────────────────────

    /// <summary>
    /// Chooses the best golf grid action from a list that includes both grid cards
    /// (to swap with the drawn card) and the drawn card itself (to discard without swapping).
    /// Strategy: swap with the worst face-up grid card if drawn card scores lower;
    /// otherwise discard the drawn card, or swap a face-down card if drawn card is good.
    /// </summary>
    private GameAction ChooseGolfGridAction(GameState state, List<GameAction> plays, string drawnCardId)
    {
        var allCards = GetCardsByIds(state, plays.Select(a => a.CardId!));
        var drawnCard = allCards.FirstOrDefault(c => c.Id == drawnCardId);
        if (drawnCard is null) return plays[_rng.Next(plays.Count)];

        var gridCards = allCards.Where(c => c.Id != drawnCardId).ToList();
        int drawnValue = GolfCardValue(drawnCard);

        // Find worst face-up grid card (highest golf value = most points = worst)
        var worstFaceUp = gridCards
            .Where(c => c.IsFaceUp)
            .OrderByDescending(c => GolfCardValue(c))
            .FirstOrDefault();

        if (worstFaceUp is not null && drawnValue < GolfCardValue(worstFaceUp))
        {
            // Drawn card is better than worst known card — swap it in
            return plays.First(a => a.CardId == worstFaceUp.Id);
        }

        // Drawn card is not better than any known grid card.
        // If it's a low-scoring card (≤3), risk swapping a face-down slot.
        var faceDownCards = gridCards.Where(c => !c.IsFaceUp).ToList();
        if (faceDownCards.Count > 0 && drawnValue <= 3)
            return plays.First(a => a.CardId == faceDownCards[_rng.Next(faceDownCards.Count)].Id);

        // Discard the drawn card (play it back without swapping)
        return plays.First(a => a.CardId == drawnCardId);
    }

    /// <summary>
    /// Golf scoring value approximation (lower is better).
    /// Jokers (-2) are not in the standard Rank enum; treated like 2s.
    /// </summary>
    private static int GolfCardValue(Card c) => c.Rank switch
    {
        Rank.Ace   => 1,
        Rank.Two   => -2,
        Rank.King  => 0,
        Rank.Ten or Rank.Jack or Rank.Queen => 10,
        _ => (int)c.Rank,  // Three=3 … Nine=9
    };

    // ── Draw-discard strategy ─────────────────────────────────────────────────

    private GameAction ChooseDraw(GameState state, IReadOnlyList<GameAction> validActions)
    {
        var discardAction = validActions.FirstOrDefault(a => a.Type == "draw_from_discard");
        if (discardAction is not null)
        {
            var discard = state.Zones.Values.FirstOrDefault(z => z.Id == "discard" || z.Id.StartsWith("discard"));
            if (discard?.TopCard is { IsFaceUp: true } top)
            {
                // Golf (grid target): compare top discard value against worst face-up grid card
                var myGrid = state.Zones.Values.FirstOrDefault(z => z.Type == "grid" && z.OwnerId == PlayerId);
                if (myGrid is not null)
                {
                    int topValue = GolfCardValue(top);
                    var worstFaceUp = myGrid.Cards.Where(c => c.IsFaceUp)
                        .OrderByDescending(c => GolfCardValue(c)).FirstOrDefault();
                    if (worstFaceUp is not null && topValue < GolfCardValue(worstFaceUp))
                        return discardAction;
                }
                else
                {
                    // Regular draw-discard: draw from discard if top card ranks below hand average
                    var hand = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");
                    if (hand is not null && hand.Count > 0)
                    {
                        double avgHandRank = hand.Cards.Average(c => (int)c.Rank);
                        if ((int)top.Rank < avgHandRank)
                            return discardAction;
                    }
                }
            }
        }

        // Default: draw from deck
        return validActions.FirstOrDefault(a => a.Type == "draw_from_deck") ?? validActions[0];
    }

    // ── Draw-discard discard strategy ────────────────────────────────────────

    /// <summary>
    /// Chooses the best card to discard in a draw-discard game (Gin Rummy, etc.).
    /// Prefers discarding high-value isolated cards: cards that aren't part of any
    /// set (same rank) or run (consecutive ranks of the same suit).
    /// </summary>
    private GameAction ChooseDiscardCard(GameState state, List<GameAction> plays)
    {
        var hand = GetCardsByIds(state, plays.Select(a => a.CardId!));
        if (hand.Count == 0) return plays[_rng.Next(plays.Count)];

        // Score each card by its "meld potential":
        // A card near a set or run is valuable; high isolated cards are good discards.
        var scores = hand.Select(c => (Card: c, Score: MeldPotential(c, hand))).ToList();

        // Discard the card with the LOWEST meld potential and HIGHEST deadwood value.
        // Tiebreak: prefer discarding higher-ranked cards.
        var worst = scores
            .OrderBy(x => x.Score)
            .ThenByDescending(x => GinDeadwoodValue(x.Card))
            .First();

        var action = plays.First(a => a.CardId == worst.Card.Id);
        return action;
    }

    /// <summary>
    /// Estimates how likely a card is to contribute to a meld.
    /// Higher = more valuable to keep. Range 0-4.
    /// </summary>
    private static int MeldPotential(Card card, List<Card> hand)
    {
        int score = 0;

        // Set potential: other cards of the same rank
        int sameRank = hand.Count(c => c.Id != card.Id && c.Rank == card.Rank);
        score += sameRank * 2;

        // Run potential: cards of the same suit within 2 ranks
        int nearRun = hand.Count(c => c.Id != card.Id && c.Suit == card.Suit
                                      && Math.Abs((int)c.Rank - (int)card.Rank) <= 2);
        score += nearRun;

        return score;
    }

    private static int GinDeadwoodValue(Card c) => c.Rank switch
    {
        Rank.Ace => 1,
        Rank.Jack or Rank.Queen or Rank.King => 10,
        _ => (int)c.Rank,
    };

    // ── Poker betting strategy ────────────────────────────────────────────────

    private GameAction ChoosePokerAction(GameState state, IReadOnlyList<GameAction> validActions)
    {
        // Check is always safe
        var check = validActions.FirstOrDefault(a => a.Type == "check");
        if (check is not null) return check;

        int handStrength = EvaluatePokerHandStrength(state);
        double callThreshold = handStrength switch
        {
            >= 4 => 0.5,   // two-pair or better: call up to 50%
            >= 2 => 0.30,  // one pair: call up to 30%
            _    => 0.12,  // high card: call up to 12%
        };

        var raise = validActions.FirstOrDefault(a => a.Type == "raise");
        var call  = validActions.FirstOrDefault(a => a.Type == "call");
        int myChips = state.GetScore(PlayerId);
        int toCall  = int.TryParse(state.Metadata.GetValueOrDefault("bet_to_call", "0"), out int tc) ? tc : 0;
        int myBet   = int.TryParse(state.Metadata.GetValueOrDefault($"bet:{PlayerId}", "0"), out int mb) ? mb : 0;
        int needed  = toCall - myBet;

        // Raise with very strong hands (two-pair or better) if affordable
        if (raise is not null && handStrength >= 4 && myChips > 0 && needed <= myChips * 0.3)
            return raise;

        if (call is not null && myChips > 0 && needed <= myChips * callThreshold)
            return call;

        // Fold if we can, else call
        return validActions.FirstOrDefault(a => a.Type == "fold")
            ?? call
            ?? validActions[_rng.Next(validActions.Count)];
    }

    /// <summary>
    /// Quick hand-strength estimate: 0=high card, 2=pair, 4=two-pair,
    /// 6=three-of-a-kind, 8=straight/flush, 10=full house+.
    /// Checks hole cards + community cards but doesn't do full 5-card evaluation.
    /// </summary>
    private int EvaluatePokerHandStrength(GameState state)
    {
        var hand      = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");
        var community = state.Zones.Values.FirstOrDefault(z => z.Type == "spread")?.Cards ?? [];
        if (hand is null) return 0;

        var allCards = hand.Cards.Concat(community).ToList();
        var byRank   = allCards.GroupBy(c => c.Rank).ToDictionary(g => g.Key, g => g.Count());

        int maxGroup = byRank.Values.DefaultIfEmpty(0).Max();
        int pairs    = byRank.Values.Count(n => n >= 2);

        if (maxGroup >= 4) return 10;       // four of a kind
        if (maxGroup == 3 && pairs >= 2) return 10; // full house
        if (maxGroup == 3) return 6;        // three of a kind
        if (pairs >= 2)    return 4;        // two pair
        if (pairs == 1)    return 2;        // one pair
        return 0;                           // high card
    }

    // ── Trump naming strategy ─────────────────────────────────────────────────

    private GameAction ChooseTrump(GameState state, IReadOnlyList<GameAction> validActions)
    {
        var hand = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");
        if (hand is null) return validActions[_rng.Next(validActions.Count)];

        // Pick the suit we hold the most cards of (among available trump choices).
        var bestAction = validActions
            .Where(a => a.Type.StartsWith("trump_"))
            .OrderByDescending(a =>
            {
                string suitName = a.Type["trump_".Length..];
                return Enum.TryParse<Suit>(suitName, ignoreCase: true, out var s)
                    ? hand.Cards.Count(c => c.Suit == s)
                    : 0;
            })
            .FirstOrDefault();

        return bestAction ?? validActions[_rng.Next(validActions.Count)];
    }

    // ── Pass-cards strategy (Hearts) ─────────────────────────────────────────

    /// <summary>
    /// Picks a card to pass: high-value cards first (hearts, Qs, high-rank off-suit),
    /// excluding cards that have already been selected for passing this round.
    /// </summary>
    private GameAction ChoosePassCard(GameState state, IReadOnlyList<GameAction> plays)
    {
        // Exclude already-selected card IDs to avoid toggle loop.
        var alreadySelected = (state.Metadata.GetValueOrDefault($"pass_selected:{PlayerId}") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToHashSet();

        var candidates = plays
            .Where(a => !alreadySelected.Contains(a.CardId!))
            .ToList();

        if (candidates.Count == 0) return plays[_rng.Next(plays.Count)];

        var hand = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");

        // Score each candidate: higher = more desirable to pass.
        // Hearts = high value to pass; Qs = very high; high off-suit ranks = moderate.
        GameAction? best = null;
        int bestScore = int.MinValue;

        foreach (var action in candidates)
        {
            var card = hand?.Cards.FirstOrDefault(c => c.Id == action.CardId);
            if (card is null) continue;

            int score;
            if (card.Rank == Rank.Queen && card.Suit == Suit.Spades) score = 1300;
            else if (card.Suit == Suit.Hearts) score = 100 + (int)card.Rank;
            else score = (int)card.Rank;  // pass highest non-heart off-suit cards

            if (score > bestScore) { bestScore = score; best = action; }
        }

        return best ?? candidates[_rng.Next(candidates.Count)];
    }

    // ── Bidding strategy ─────────────────────────────────────────────────────

    private GameAction ChooseBid(GameState state, IReadOnlyList<GameAction> validActions)
    {
        var hand = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");
        if (hand is null) return validActions[_rng.Next(validActions.Count)];
        var cards = hand.Cards;

        // accept_or_pass (Euchre): accept if we hold 2+ trump-suited cards
        if (validActions.Any(a => a.Type == "bid_accept"))
        {
            var kitty = state.FindZone("kitty");
            if (kitty?.TopCard is { } top)
            {
                int trumpCount = cards.Count(c => c.Suit == top.Suit);
                if (trumpCount >= 2) return validActions.First(a => a.Type == "bid_accept");
            }
            return validActions.First(a => a.Type == "bid_pass");
        }

        // suit_or_pass (Euchre second round): pick suit we hold most of
        bool allSuitOrPass = validActions.All(a =>
            a.Type is "bid_clubs" or "bid_diamonds" or "bid_hearts" or "bid_spades" or "bid_pass");
        if (allSuitOrPass)
        {
            string? excluded = state.Metadata.GetValueOrDefault("bid_excluded_suit");
            var bestSuit = Enum.GetValues<Suit>()
                .Where(s => s.ToString().ToLower() != excluded)
                .OrderByDescending(s => cards.Count(c => c.Suit == s))
                .First();
            if (cards.Count(c => c.Suit == bestSuit) >= 3)
            {
                string suitType = $"bid_{bestSuit.ToString().ToLower()}";
                var suitAct = validActions.FirstOrDefault(a => a.Type == suitType);
                if (suitAct is not null) return suitAct;
            }
            return validActions.FirstOrDefault(a => a.Type == "bid_pass")
                ?? validActions[_rng.Next(validActions.Count)];
        }

        // number-style (Spades, Pinochle): estimate trick count from hand strength
        // Check for nil option first: bid nil if hand is very weak (no aces or kings)
        var nilBid = validActions.FirstOrDefault(a => a.Type == "bid_nil");
        if (nilBid is not null)
        {
            bool hasAce = cards.Any(c => c.Rank == Rank.Ace);
            bool hasKing = cards.Any(c => c.Rank == Rank.King);
            bool hasHighSpade = cards.Any(c => c.Suit == Suit.Spades && (int)c.Rank >= (int)Rank.Queen);
            if (!hasAce && !hasKing && !hasHighSpade)
                return nilBid;
        }

        var numberBids = validActions
            .Where(a => a.Type.StartsWith("bid_") && int.TryParse(a.Type["bid_".Length..], out _))
            .OrderBy(a => int.Parse(a.Type["bid_".Length..]))
            .ToList();

        if (numberBids.Count > 0)
        {
            string? trumpName = state.Metadata.GetValueOrDefault("trick_trump")
                             ?? state.Metadata.GetValueOrDefault("bid_trump");
            Suit? trump = trumpName is not null
                ? Enum.GetValues<Suit>().Cast<Suit?>().FirstOrDefault(
                    s => string.Equals(s!.Value.ToString(), trumpName, StringComparison.OrdinalIgnoreCase))
                : null;

            // Score: A=3, K=2, Q=1, J=0.5; trump cards get +0.5
            double score = cards.Sum(c =>
            {
                double v = c.Rank switch { Rank.Ace => 3.0, Rank.King => 2.0,
                                           Rank.Queen => 1.0, Rank.Jack => 0.5, _ => 0.0 };
                if (trump.HasValue && c.Suit == trump.Value) v += 0.5;
                return v;
            });

            int estimated = (int)Math.Round(score / 3.0);
            int minBid    = int.Parse(numberBids.First().Type["bid_".Length..]);
            int maxBid    = int.Parse(numberBids.Last().Type["bid_".Length..]);
            int target    = Math.Clamp(estimated, minBid, maxBid);
            return numberBids.OrderBy(a => Math.Abs(int.Parse(a.Type["bid_".Length..]) - target)).First();
        }

        return validActions[_rng.Next(validActions.Count)];
    }

    // ── Trick analysis helpers ────────────────────────────────────────────────

    private static List<Card> GetTrickCards(GameState state)
        => state.Zones.Values.FirstOrDefault(z => z.Type == "trick")?.Cards.ToList() ?? [];

    /// <summary>Returns the currently-winning card in the trick, or null if trick is empty.</summary>
    private static Card? FindTrickWinner(List<Card> trickCards, Suit? trump)
    {
        if (trickCards.Count == 0) return null;
        Suit ledSuit = trickCards[0].Suit;
        Card? winner = trickCards[0];
        foreach (var c in trickCards.Skip(1))
        {
            if (trump.HasValue && c.Suit == trump && winner!.Suit != trump)
                winner = c;                                       // trumped
            else if (c.Suit == winner!.Suit && (int)c.Rank > (int)winner.Rank)
                winner = c;                                       // higher in same suit
        }
        return winner;
    }

    private static bool CanBeat(Card mine, Card winner, Suit? trump)
    {
        if (trump.HasValue)
        {
            if (mine.Suit == trump && winner.Suit != trump) return true;   // I trump a non-trump winner
            if (mine.Suit != trump && winner.Suit == trump) return false;  // winner is trump, I'm not
        }
        return mine.Suit == winner.Suit && (int)mine.Rank > (int)winner.Rank;
    }

    private static bool IsWinnerPointFree(List<Card> trickCards)
        => trickCards.All(c => !IsPointCard(c));

    private static bool IsPointCard(Card c)
        => c.Suit == Suit.Hearts || (c.Suit == Suit.Spades && c.Rank == Rank.Queen);

    private static int PointValue(Card c) => c.Suit == Suit.Hearts ? 1
        : (c.Suit == Suit.Spades && c.Rank == Rank.Queen) ? 13
        : 0;

    // ── State reading helpers ─────────────────────────────────────────────────

    private List<Card> GetCardsByIds(GameState state, IEnumerable<string> ids)
    {
        var idSet = ids.ToHashSet();
        return state.Zones.Values
            .SelectMany(z => z.Cards)
            .Where(c => idSet.Contains(c.Id))
            .ToList();
    }

    private static Suit? GetTrump(GameState state)
    {
        string? name = state.Metadata.GetValueOrDefault("trick_trump")
                    ?? state.Metadata.GetValueOrDefault("bid_trump");
        return name is null ? null : ParseSuit(name);
    }

    private static Suit? ParseSuit(string name) => name.ToLowerInvariant() switch
    {
        "spades"   => Suit.Spades,
        "hearts"   => Suit.Hearts,
        "diamonds" => Suit.Diamonds,
        "clubs"    => Suit.Clubs,
        _          => null,
    };
}
