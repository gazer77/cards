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

        // Card-play decisions (trick-taking)
        var plays = validActions.Where(a => a.Type == "play_card" && a.CardId is not null).ToList();
        if (plays.Count > 0)
            return ChooseTrickCard(state, plays);

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

    // ── Draw-discard strategy ─────────────────────────────────────────────────

    private GameAction ChooseDraw(GameState state, IReadOnlyList<GameAction> validActions)
    {
        // Draw from discard if top discard card is lower rank than average hand rank.
        var discardAction = validActions.FirstOrDefault(a => a.Type == "draw_from_discard");
        if (discardAction is not null)
        {
            var discard = state.Zones.Values.FirstOrDefault(z => z.Id == "discard" || z.Id.StartsWith("discard"));
            var hand    = state.FindZone($"hand:{PlayerId}") ?? state.FindZone("hand");
            if (discard?.TopCard is { IsFaceUp: true } top && hand is not null && hand.Count > 0)
            {
                double avgHandRank = hand.Cards.Average(c => (int)c.Rank);
                if ((int)top.Rank < avgHandRank)
                    return discardAction;
            }
        }

        // Default: draw from deck
        return validActions.FirstOrDefault(a => a.Type == "draw_from_deck") ?? validActions[0];
    }

    // ── Poker betting strategy ────────────────────────────────────────────────

    private GameAction ChoosePokerAction(GameState state, IReadOnlyList<GameAction> validActions)
    {
        // Check is always safe
        var check = validActions.FirstOrDefault(a => a.Type == "check");
        if (check is not null) return check;

        // Call if pot odds are reasonable (call size ≤ 20% of chips)
        var call = validActions.FirstOrDefault(a => a.Type == "call");
        if (call is not null)
        {
            int myChips = state.GetScore(PlayerId);
            int pot     = int.TryParse(state.Metadata.GetValueOrDefault("pot", "0"), out int p) ? p : 0;
            int toCall  = int.TryParse(state.Metadata.GetValueOrDefault("bet_to_call", "0"), out int tc) ? tc : 0;
            int myBet   = int.TryParse(state.Metadata.GetValueOrDefault($"bet:{PlayerId}", "0"), out int mb) ? mb : 0;
            int needed  = toCall - myBet;

            if (myChips > 0 && needed <= myChips * 0.2)
                return call;
        }

        // Fold if we can, else call
        return validActions.FirstOrDefault(a => a.Type == "fold")
            ?? call
            ?? validActions[_rng.Next(validActions.Count)];
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
