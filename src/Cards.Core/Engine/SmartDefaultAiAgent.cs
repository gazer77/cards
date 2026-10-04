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
    private readonly IRandomSource _rng;

    public string PlayerId { get; }

    /// <summary>
    /// Plays at <see cref="Difficulty.Hard"/>: the sharper choices below that normal leaves
    /// out. Off, the agent plays exactly as it always has.
    /// </summary>
    public bool Hard { get; init; }

    public SmartDefaultAiAgent(string playerId, IRandomSource rng)
    {
        PlayerId = playerId;
        _rng     = rng;
    }

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

        // Melding within the turn (Hand and Foot): pick, lay, then discard.
        if (state.Metadata.GetValueOrDefault("dd_turn_state") == "discard" && MeldsInDiscardTurn(state))
            return ChooseMeldTurn(state, validActions);

        // Card-play decisions — detect context before generic trick-taking
        var plays = validActions.Where(a => a.Type == "play_card" && a.CardId is not null).ToList();
        if (plays.Count > 0)
        {
            // Pass-cards phase (Hearts): pick high-value cards not already selected.
            if (state.Metadata.ContainsKey("pass_direction"))
                return ChoosePassCard(state, plays);

            // Cribbage: laying away to the crib, and the play.
            if (state.Metadata.ContainsKey("crib_laying"))
                return ChooseCribDiscard(state, plays);
            if (state.Metadata.ContainsKey("peg_count"))
                return ChoosePeg(state, plays);

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

        // Blackjack — basic strategy, not a coin toss. Random play was harmless while
        // the choice was hit or stand; with split and surrender on offer it threw hands
        // away at random.
        if (validActions.Any(a => a.Type == "hit") && validActions.Any(a => a.Type == "stand"))
            return ChooseBlackjackAction(state, validActions);

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


    // ── Melding inside the turn (Hand and Foot) ───────────────────────────────

    /// <summary>Lays already tried that the table would not take.</summary>
    private readonly HashSet<string> _refusedLays = [];

    /// <summary>Whether this phase lays melds between the draw and the discard.</summary>
    private static bool MeldsInDiscardTurn(GameState state)
    {
        var phase = state.Definition?.Phases.FirstOrDefault(p => p.Id == state.CurrentPhaseId);
        if (phase?.Extra?.TryGetValue("special_actions", out var el) != true
            || el.ValueKind != System.Text.Json.JsonValueKind.Array) return false;
        return el.EnumerateArray().Any(e => e.GetString() is "meld" or "add_to_meld");
    }

    /// <summary>
    /// A turn in which melds are laid by picking cards and pressing a button, the way a
    /// person does it: one card picked per action, then the lay, then the discard. The
    /// agent used to see only the discards, so it never laid a card and a round against
    /// it ended only when the stock ran out.
    /// </summary>
    private GameAction ChooseMeldTurn(GameState state, IReadOnlyList<GameAction> actions)
    {
        // Going out is the point of the round.
        if (actions.FirstOrDefault(a => a.Type == "go_out") is { } goOut) return goOut;

        var hand = state.Zones.GetValueOrDefault($"hand:{PlayerId}")?.Cards.ToList() ?? [];
        var selected = (state.Metadata.GetValueOrDefault("selected_card") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => int.TryParse(t, out var uid) ? hand.FirstOrDefault(c => c.Uid == uid) : null)
            .OfType<Card>()
            .ToList();

        var plan = PlanLay(state, hand);
        string? key = plan is null ? null
            : $"{state.RoundNumber}|{hand.Count}|{string.Join(",", plan.Select(c => c.Uid).Order())}";

        if (plan is not null && !_refusedLays.Contains(key!))
        {
            // The lay is offered for exactly what was planned: press it. A button offered
            // for some other pick — cards tapped by someone else — is not taken on trust.
            if (selected.Count == plan.Count && plan.All(selected.Contains)
                && actions.FirstOrDefault(a => a.Type is "meld" or "add_to_meld") is { } lay)
                return lay;

            if (selected.FirstOrDefault(c => !plan.Contains(c)) is { } stray)
                return new GameAction("select_card", CardId: stray.Id, CardUid: stray.Uid);
            if (plan.FirstOrDefault(c => !selected.Contains(c)) is { } next)
                return new GameAction("select_card", CardId: next.Id, CardUid: next.Uid);

            // Everything planned is picked and the table offers no lay: the plan was
            // wrong about a rule. Remember it, so the next look discards instead of
            // picking the same cards for ever.
            _refusedLays.Add(key!);
        }

        var plays = actions.Where(a => a.Type == "play_card" && a.CardId is not null).ToList();
        return plays.Count > 0 ? ChooseMeldingDiscard(state, hand, plays) : actions[0];
    }

    /// <summary>
    /// The cards worth laying this turn, or null. Additions to melds already down come
    /// first, a rank at a time, since the table takes one addition per press; then new
    /// melds, all together, because an opening minimum is met by what goes down at once.
    /// Keeps a card to discard unless emptying the hand brings up the foot.
    /// </summary>
    private List<Card>? PlanLay(GameState state, List<Card> hand)
    {
        if (hand.Count == 0 || state.Definition is not { } def) return null;

        var wilds      = MeldRules.WildRanks(def);
        var unmeldable = MeldRules.UnmeldableRanks(state);
        int bookSize   = ScoringEngine.BookSize(def);
        var team       = state.GetPlayerTeam(PlayerId);
        var melds      = (team is not null ? state.Zones.GetValueOrDefault($"meld:{team.Id}") : null)
                      ?? state.Zones.GetValueOrDefault($"meld:{PlayerId}");
        if (melds is null) return null;

        bool opened   = MeldRules.HasOpened(melds, state);
        bool footLeft = int.TryParse(state.Metadata.GetValueOrDefault($"zone_count:foot:{PlayerId}"), out var f) && f > 0;

        bool IsWild(Card c) => MeldRules.IsWild(c, wilds);
        var naturals  = hand.Where(c => !IsWild(c) && !unmeldable.Contains(c.Rank)).ToList();
        var wildCards = hand.Where(IsWild).ToList();

        var groups = Enumerable.Range(0, melds.Groups.Count)
            .Select(i => melds.GroupCards(i))
            .Where(g => MeldRules.IsMeldGroup(g, wilds, unmeldable))
            .ToList();
        Rank RankOf(IReadOnlyList<Card> g) => MeldRules.MeldRankOf(g, wilds);

        // The table's rule: with the foot played, a lay leaves two cards — one to discard,
        // one to hold — unless the side can already go out, when anything may go down.
        // Before the foot, a lay may empty the hand, which picks the foot up.
        bool canGoOut = SideCanGoOut(state);
        bool Keeps(int laying) => footLeft || canGoOut || hand.Count - laying >= 2;

        // Where wilds meld on their own and the side has no wild meld yet, wilds are held
        // back for one: spent one at a time on pairs and dirty melds, three never gather.
        bool hoardWilds = MeldRules.WildMeldsAllowed(state) && !groups.Any(g => MeldRules.IsAllWild(g, wilds));

        if (opened)
        {
            // Naturals that belong on a meld already down.
            foreach (var g in groups.OrderByDescending(g => g.Count))
            {
                var same = naturals.Where(c => c.Rank == RankOf(g)).ToList();
                if (same.Count > 0 && Keeps(same.Count)) return same;
            }

            // Where wilds meld on their own, they build toward a wild book: started with
            // three or more, fed until it is a book. Where going out asks for one, it is
            // the scarcest thing on the table, so it comes before the dirty books.
            if (MeldRules.WildMeldsAllowed(state) && wildCards.Count > 0)
            {
                var wildMeld = groups.FirstOrDefault(g => MeldRules.IsAllWild(g, wilds));
                if (wildMeld is not null && wildMeld.Count < bookSize)
                {
                    int n = Math.Min(wildCards.Count, bookSize - wildMeld.Count);
                    if (Keeps(n)) return wildCards.Take(n).ToList();
                }
                if (wildMeld is null && wildCards.Count >= 3)
                {
                    int n = Math.Min(wildCards.Count, bookSize);
                    if (Keeps(n)) return wildCards.Take(n).ToList();
                }
            }

            // Wilds, when they finish a book or go on a meld already dirty. The table puts
            // an all-wild addition on its biggest legal meld, so that is the one judged.
            // Not while they are being gathered for a wild meld.
            if (wildCards.Count > 0 && !hoardWilds)
            {
                var target = groups
                    .Where(g => g.Count(IsWild) < g.Count(c => !IsWild(c)) && g.Count < bookSize)
                    .OrderByDescending(g => g.Count)
                    .FirstOrDefault();
                if (target is not null)
                {
                    int room  = target.Count(c => !IsWild(c)) - target.Count(IsWild);
                    int needs = bookSize - target.Count;
                    int n     = Math.Min(room, Math.Min(wildCards.Count, needs));
                    if (n > 0 && (target.Any(IsWild) || n >= needs) && Keeps(n))
                        return wildCards.Take(n).ToList();
                }
            }
        }

        // New melds: every rank held three deep that is not down already. Pairs join
        // with a wild each, when the side is down or the opening needs the points.
        var byRank = naturals.GroupBy(c => c.Rank)
            .Where(g => !groups.Any(m => RankOf(m) == g.Key))
            .OrderByDescending(g => g.Count())
            .ToList();
        var lay = byRank.Where(g => g.Count() >= 3).SelectMany(g => g).ToList();

        int required = !opened && int.TryParse(state.Metadata.GetValueOrDefault("dd_opening_requirement"), out var r) ? r : 0;
        int Worth(List<Card> cards) => ScoringEngine.CardPointValue(def, cards);

        // Wilds gathered for a wild meld are spent here only to meet an opening.
        var spare = new Queue<Card>(hoardWilds && required == 0 ? [] : wildCards);
        foreach (var pair in byRank.Where(g => g.Count() == 2))
        {
            bool shortOfOpening = required > 0 && Worth(lay) < required;
            if (spare.Count == 0 || !(shortOfOpening || opened)) break;
            lay.AddRange(pair);
            lay.Add(spare.Dequeue());
        }

        // Still short of the opening: a spare wild on each meld that can carry one.
        if (required > 0)
        {
            foreach (var g in lay.Where(c => !IsWild(c)).GroupBy(c => c.Rank).ToList())
            {
                if (Worth(lay) >= required || spare.Count == 0) break;
                lay.Add(spare.Dequeue());
            }
            if (Worth(lay) < required) return null;
        }

        // Keep a card back when the lay would take the whole hand for nothing: drop the
        // smallest meld, and any wild left with nothing to stand in for.
        while (lay.Count > 0 && !Keeps(lay.Count))
        {
            var smallest = lay.Where(c => !IsWild(c)).GroupBy(c => c.Rank).OrderBy(g => g.Count()).First();
            lay.RemoveAll(c => !IsWild(c) && c.Rank == smallest.Key);
            while (lay.Count(IsWild) > 0 && MeldRules.PartitionIntoMelds(lay, wilds) is null)
                lay.Remove(lay.Last(IsWild));
            if (required > 0 && Worth(lay) < required) return null;
        }

        return lay.Count > 0 && MeldRules.PartitionIntoMelds(lay, wilds) is not null ? lay : null;
    }

    /// <summary>
    /// Whether this seat's side meets the game's own condition for going out
    /// (<c>go_out_condition</c> — Hand and Foot's two books), read from the definition.
    /// </summary>
    private static bool SideCanGoOut(GameState state)
    {
        var phase = state.Definition?.Phases.FirstOrDefault(p => p.Id == state.CurrentPhaseId);
        return phase?.Extra?.TryGetValue("go_out_condition", out var condition) == true
            && condition.ValueKind == System.Text.Json.JsonValueKind.Object
            && RuleCondition.Evaluate(condition, state);
    }

    /// <summary>
    /// A discard for a game that melds sets: an unmeldable rank first (Hand and Foot's
    /// black threes are good for nothing else), never a wild, then the loneliest rank,
    /// cheapest first.
    /// </summary>
    private static GameAction ChooseMeldingDiscard(GameState state, List<Card> hand, List<GameAction> plays)
    {
        var wilds      = MeldRules.WildRanks(state.Definition);
        var unmeldable = MeldRules.UnmeldableRanks(state);
        var offered    = hand.Where(c => plays.Any(p => p.CardId == c.Id)).ToList();
        if (offered.Count == 0 || state.Definition is not { } def) return plays[0];

        var choice = offered
            .OrderByDescending(c => unmeldable.Contains(c.Rank) && !MeldRules.IsWild(c, wilds))
            .ThenBy(c => MeldRules.IsWild(c, wilds))
            .ThenBy(c => hand.Count(h => h.Rank == c.Rank))
            .ThenBy(c => ScoringEngine.CardPointValue(def, [c]))
            .First();
        return plays.First(p => p.CardId == choice.Id);
    }

    // ── Blackjack strategy ────────────────────────────────────────────────────

    /// <summary>
    /// A compact basic strategy: the plays a careful player makes from their own total
    /// and the dealer's up-card, and nothing cleverer. Not perfect — no deviations, no
    /// counting — but it never splits tens or stands on eight.
    /// </summary>
    private GameAction ChooseBlackjackAction(GameState state, IReadOnlyList<GameAction> actions)
    {
        GameAction? Offer(string type) => actions.FirstOrDefault(a => a.Type == type);

        // The hand in play — the split hand once the first is finished.
        string active = state.Metadata.GetValueOrDefault($"bj_active:{PlayerId}", "hand");
        var zone = active == "split"
            ? state.Zones.GetValueOrDefault($"split:{PlayerId}")
            : state.Zones.GetValueOrDefault($"hand:{PlayerId}");
        var dealer = state.Players.FirstOrDefault(p => p.Role is not null);
        var upCard = dealer is null ? null
            : state.Zones.GetValueOrDefault($"hand:{dealer.Id}")?.Cards.FirstOrDefault(c => c.IsFaceUp);

        if (zone is null || upCard is null) return Offer("stand") ?? actions[0];

        var cards = zone.Cards.ToList();
        var (total, soft) = BlackjackTotal(cards);
        int up = upCard.Rank == Rank.Ace ? 11 : upCard.Rank >= Rank.Jack ? 10 : (int)upCard.Rank;

        // Pairs: always aces and eights, never tens, fives or fours; the rest against a weak dealer.
        if (Offer("split") is { } split && cards.Count == 2)
        {
            var rank = cards[0].Rank;
            int pip  = rank == Rank.Ace ? 11 : rank >= Rank.Jack ? 10 : (int)rank;
            if (rank is Rank.Ace or Rank.Eight) return split;
            if (pip is 2 or 3 or 6 or 7 && up is >= 2 and <= 7) return split;
            if (pip == 9 && up is >= 2 and <= 9 && up != 7) return split;
        }

        // Surrender a hard sixteen against a nine, ten or ace — the one hand worse than half.
        if (Offer("surrender") is { } surrender && !soft && total == 16 && up >= 9)
            return surrender;

        // Doubles: eleven always, ten against anything short of a ten, nine against a
        // weak dealer, and the soft middle against a five or six.
        if (Offer("double_down") is { } dbl)
        {
            if (!soft && total == 11) return dbl;
            if (!soft && total == 10 && up <= 9) return dbl;
            if (!soft && total == 9 && up is >= 3 and <= 6) return dbl;
            if (soft && total is >= 13 and <= 18 && up is 5 or 6) return dbl;
        }

        bool hit = soft
            ? total <= 17 || (total == 18 && up >= 9)
            : total <= 11
              || (total == 12 && (up <= 3 || up >= 7))
              || (total is >= 13 and <= 16 && up >= 7);

        return (hit ? Offer("hit") : Offer("stand")) ?? actions[0];
    }

    private static (int Total, bool Soft) BlackjackTotal(IEnumerable<Card> cards)
    {
        int total = 0, aces = 0;
        foreach (var c in cards)
        {
            if      (c.Rank == Rank.Ace)  { aces++; total += 11; }
            else if (c.Rank >= Rank.Jack) total += 10;
            else                          total += (int)c.Rank;
        }
        while (total > 21 && aces > 0) { total -= 10; aces--; }
        return (total, aces > 0);
    }
    // ── Trick-taking strategy ─────────────────────────────────────────────────

    private GameAction ChooseTrickCard(GameState state, List<GameAction> plays)
    {
        var myCards = GetCardsByIds(state, plays.Select(a => a.CardId!));
        if (myCards.Count == 0) return plays[_rng.Next(plays.Count)];

        Suit? trump    = GetTrump(state);
        // Avoiding points is Hearts' game: points taken in cards. Every trick game marks
        // hearts broken when one is played, and reading that as "this is Hearts" turned
        // Spades, Whist and Euchre into games of losing tricks after the first heart.
        bool isHearts  = state.Definition?.Scoring?.Type == "card_points" || state.GameId == "hearts";
        if (Hard) return ChooseTrickCardHard(state, plays, myCards, trump, isHearts);
        var trickCards = GetTrickCards(state);
        bool leading   = trickCards.Count == 0;

        Card chosen = leading
            ? ChooseLeadCard(myCards, trump, isHearts)
            : ChooseFollowCard(myCards, trickCards, trump, isHearts);

        return plays.First(a => a.CardId == chosen.Id);
    }

    // ── Hard: trick-taking ────────────────────────────────────────────────────

    /// <summary>Cards this player has seen played this deal, by suit and rank.</summary>
    private readonly HashSet<(Suit, Rank)> _seen = [];
    private int _seenRound = -1, _seenHand = int.MaxValue;

    /// <summary>
    /// Notes what is on the table. A new deal — a new round, or a hand that has grown —
    /// starts the memory afresh. It sees what is in the trick when it is asked to play,
    /// so it misses the cards played after it to a trick; it treats those as unseen, and
    /// so errs towards thinking a high card is still out.
    /// </summary>
    private void Remember(GameState state, List<Card> hand)
    {
        if (state.RoundNumber != _seenRound || hand.Count > _seenHand)
        {
            _seen.Clear();
            _seenRound = state.RoundNumber;
        }
        _seenHand = hand.Count;
        foreach (var (_, c) in TrickPlays(state)) _seen.Add((c.Suit, c.Rank));
    }

    /// <summary>No card of its suit that could beat it is still out: every higher one is seen or ours.</summary>
    private bool IsMaster(Card card, List<Card> hand)
        => Enum.GetValues<Rank>().Where(r => r > card.Rank && r != Rank.Joker)
            .All(r => _seen.Contains((card.Suit, r)) || hand.Any(h => h.Suit == card.Suit && h.Rank == r));

    private GameAction ChooseTrickCardHard(GameState state, List<GameAction> plays, List<Card> legal, Suit? trump, bool hearts)
    {
        var hand  = state.FindZone($"hand:{PlayerId}")?.Cards.ToList() ?? legal;
        Remember(state, hand);

        var trick = TrickPlays(state);
        Card chosen = hearts
            ? ChooseHeartsCardHard(state, legal, hand, trick)
            : ChoosePartnershipCardHard(state, legal, hand, trick, trump);
        return plays.First(a => a.CardId == chosen.Id);
    }

    /// <summary>
    /// Spades, Euchre, Whist, Pinochle. Leads a card nothing still out can beat, or low from
    /// its longest side suit. Following: lets a partner's winning card stand; second to
    /// play, ducks unless it holds a sure winner; last, wins as cheaply as it can; void,
    /// trumps an opponent's trick with its smallest trump that wins and otherwise throws
    /// its least useful card.
    /// </summary>
    private Card ChoosePartnershipCardHard(GameState state, List<Card> legal, List<Card> hand,
                                           List<(string Owner, Card Card)> trick, Suit? trump)
    {
        int players     = state.Players.Count(p => p.Role is null);
        bool last       = trick.Count == players - 1;
        string? partner = state.GetPlayerTeam(PlayerId)?.PlayerIds.FirstOrDefault(id => id != PlayerId);

        // The least useful card: lowest, and not a trump while there is anything else.
        Card Cheapest(IEnumerable<Card> cards)
            => cards.OrderBy(c => c.Suit == trump ? 1 : 0).ThenBy(c => (int)c.Rank).First();

        if (trick.Count == 0)
        {
            var sure = legal.Where(c => c.Suit != trump && IsMaster(c, hand)).ToList();
            if (sure.Count > 0)
                return sure.OrderByDescending(c => hand.Count(h => h.Suit == c.Suit)).First();

            var side = legal.Where(c => c.Suit != trump).ToList();
            return side.Count > 0 ? side.OrderByDescending(c => (int)c.Rank).First() : Cheapest(legal);
        }

        var winner  = FindTrickWinner(trick.Select(t => t.Card).ToList(), trump)!;
        var owner   = trick.First(t => ReferenceEquals(t.Card, winner)).Owner;
        bool ours   = partner is not null && owner == partner;
        var beaters = legal.Where(c => CanBeat(c, winner, trump))
                           .OrderBy(c => c.Suit == trump ? 1 : 0).ThenBy(c => (int)c.Rank).ToList();

        // Partner has it: leave it, unless they could still be beaten and we can make sure.
        if (ours && (last || IsMaster(winner, hand) || beaters.Count == 0))
            return Cheapest(legal);

        if (beaters.Count == 0) return Cheapest(legal);
        if (last) return beaters[0];

        var sureBeater = beaters.FirstOrDefault(c => IsMaster(c, hand) || (c.Suit == trump && winner.Suit != trump));
        if (sureBeater is not null) return sureBeater;

        return beaters[0];
    }

    /// <summary>
    /// Hearts. Leads low from a short suit, and spades while the queen is out and it holds
    /// nothing she would catch. Following, gets rid of its highest card that still loses;
    /// void, sheds the queen of spades, then the high spades she would catch, then hearts,
    /// highest first.
    /// </summary>
    private Card ChooseHeartsCardHard(GameState state, List<Card> legal, List<Card> hand,
                                      List<(string Owner, Card Card)> trick)
    {
        bool queenOut = !_seen.Contains((Suit.Spades, Rank.Queen)) && !hand.Any(IsQueenOfSpades);
        bool safeSpades = !hand.Any(c => c.Suit == Suit.Spades && c.Rank >= Rank.Queen);

        if (trick.Count == 0)
        {
            if (queenOut && safeSpades && legal.Any(c => c.Suit == Suit.Spades))
                return legal.Where(c => c.Suit == Suit.Spades).OrderBy(c => (int)c.Rank).First();

            return legal.OrderBy(c => c.Suit == Suit.Hearts ? 1 : 0)
                        .ThenBy(c => hand.Count(h => h.Suit == c.Suit))
                        .ThenBy(c => (int)c.Rank).First();
        }

        var led    = trick[0].Card.Suit;
        var winner = FindTrickWinner(trick.Select(t => t.Card).ToList(), null)!;

        if (legal.Any(c => c.Suit == led))
        {
            var losers = legal.Where(c => !CanBeat(c, winner, null)).ToList();
            if (losers.Count > 0) return losers.OrderByDescending(c => (int)c.Rank).First();

            // It will win whatever it plays: last to play, take it with the highest; with
            // others still to come, the lowest, hoping someone goes over.
            bool last = trick.Count == state.Players.Count - 1;
            var pick = last ? legal.OrderByDescending(c => (int)c.Rank).First() : legal.OrderBy(c => (int)c.Rank).First();
            return IsQueenOfSpades(pick) && legal.Count > 1 ? legal.Where(c => !IsQueenOfSpades(c)).OrderBy(c => (int)c.Rank).First() : pick;
        }

        // Void: shed what hurts most.
        return legal.OrderByDescending(c => IsQueenOfSpades(c) ? 3
                                          : c.Suit == Suit.Spades && c.Rank > Rank.Queen && queenOut ? 2
                                          : c.Suit == Suit.Hearts ? 1 : 0)
                    .ThenByDescending(c => (int)c.Rank).First();
    }

    private static bool IsQueenOfSpades(Card c) => c.Suit == Suit.Spades && c.Rank == Rank.Queen;

    /// <summary>
    /// Hearts, passing at hard: the queen of spades and the ace and king that catch her —
    /// unless enough low spades guard them — high hearts, and the cards of a short side
    /// suit, to go void in it and shed points there later.
    /// </summary>
    private static int HardPassScore(Card card, List<Card> hand)
    {
        int spades    = hand.Count(c => c.Suit == Suit.Spades);
        bool guarded  = hand.Count(c => c.Suit == Suit.Spades && c.Rank < Rank.Queen) >= 3;
        int  length   = hand.Count(c => c.Suit == card.Suit);

        if (IsQueenOfSpades(card)) return guarded && spades >= 5 ? 50 : 1300;
        if (card.Suit == Suit.Spades && card.Rank > Rank.Queen) return guarded ? 40 : 1100;
        if (card.Suit == Suit.Hearts) return 200 + (int)card.Rank * 10;
        if (card.Suit != Suit.Spades && length <= 2) return 500 + (int)card.Rank;   // void a short suit
        return (int)card.Rank * 10;
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
        {
            // Chosen once. Rolling the die inside the predicate compared each play to a
            // different card and could match none — unreachable while every grid card
            // was dealt face-up, which is the only reason it went unnoticed.
            var target = faceDownCards[_rng.Next(faceDownCards.Count)];
            return plays.First(a => a.CardId == target.Id);
        }

        // Discard the drawn card (play it back without swapping)
        return plays.First(a => a.CardId == drawnCardId);
    }

    /// <summary>
    /// Golf scoring value approximation (lower is better).
    /// </summary>
    private static int GolfCardValue(Card c) => c.Rank switch
    {
        Rank.Joker => -2,
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

    // ── Cribbage ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Lays away the cards that leave the best four: every way of keeping four is
    /// counted as a hand would be (without knowing the starter), and what goes to the
    /// crib counts for us in our own crib and against us in theirs. Picks one card of
    /// the best discard at a time, as the phase asks for them.
    /// </summary>
    private GameAction ChooseCribDiscard(GameState state, IReadOnlyList<GameAction> plays)
    {
        var hand = state.FindZone($"hand:{PlayerId}")?.Cards.ToList() ?? [];
        var picked = (state.Metadata.GetValueOrDefault("selected_card") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToHashSet();
        int owed = hand.Count - 4;
        if (owed <= 0 || hand.Count > 8) return plays[0];

        bool myCrib = state.DealerId == PlayerId;
        IReadOnlyList<Card>? best = null;
        double bestScore = double.MinValue;

        // Hard weighs every starter that could still turn up, not only the four in hand.
        var starters = Hard ? AllCards().Where(s => !hand.Any(h => h.Suit == s.Suit && h.Rank == s.Rank)).ToList() : null;

        foreach (var keep in Combinations(hand, 4))
        {
            var away  = hand.Except(keep).ToList();
            double kept = starters is null
                ? CribbageScore.Show(keep, null, crib: false).Total
                : starters.Average(s => CribbageScore.Show(keep, s, crib: false).Total);
            int crib  = CribbageScore.Show(away, null, crib: true).Total
                      + away.Count(c => c.Rank == Rank.Five) * 2;   // fives make fifteens with the tens to come
            double score = kept * 2 + (myCrib ? crib : -crib);
            if (score > bestScore) { bestScore = score; best = away; }
        }

        var next = best?.FirstOrDefault(c => !picked.Contains(c.Uid));
        return plays.FirstOrDefault(a => a.CardId == next?.Id) ?? plays[0];
    }

    private static IEnumerable<Card> AllCards()
        => from s in new[] { Suit.Clubs, Suit.Diamonds, Suit.Hearts, Suit.Spades }
           from r in Enum.GetValues<Rank>().Where(r => r != Rank.Joker)
           select new Card(s, r);

    private static IEnumerable<List<Card>> Combinations(List<Card> cards, int k)
    {
        if (k == 0) { yield return []; yield break; }
        for (int i = 0; i <= cards.Count - k; i++)
            foreach (var rest in Combinations(cards.Skip(i + 1).ToList(), k - 1))
                yield return [cards[i], .. rest];
    }

    /// <summary>
    /// Plays for the most points now, and otherwise avoids giving them away: not leaving
    /// the count on five or twenty-one (a ten makes fifteen or thirty-one), and not
    /// leading a five. Among equal choices, holds on to low cards for the end of a count.
    /// </summary>
    private GameAction ChoosePeg(GameState state, IReadOnlyList<GameAction> plays)
    {
        var hand  = state.FindZone($"hand:{PlayerId}")?.Cards.ToList() ?? [];
        int count = int.TryParse(state.Metadata.GetValueOrDefault("peg_count"), out int c) ? c : 0;
        var all   = state.Zones.Values.SelectMany(z => z.Cards).ToList();
        var run   = (state.Metadata.GetValueOrDefault("peg_run") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(u => all.FirstOrDefault(x => x.Uid == int.Parse(u)))
            .Where(x => x is not null).Select(x => x!).ToList();

        GameAction? best = null;
        int bestScore = int.MinValue;
        foreach (var play in plays)
        {
            var card = hand.FirstOrDefault(x => x.Id == play.CardId);
            if (card is null) continue;

            int next  = count + CribbageScore.Value(card);
            int score = CribbageScore.Peg([.. run, card]).Total * 10;
            if (next is 5 or 21) score -= 6;
            if (count == 0 && card.Rank == Rank.Five) score -= 4;
            score += CribbageScore.Value(card);   // spend the high cards, keep the low ones

            if (score > bestScore) { bestScore = score; best = play; }
        }
        return best ?? plays[0];
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
            if (Hard && hand is not null) score = HardPassScore(card, hand.Cards.ToList());
            else if (card.Rank == Rank.Queen && card.Suit == Suit.Spades) score = 1300;
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
        // bid_alone may appear alongside suit bids (going_alone option); include it so the check
        // correctly identifies this context even when the loner option is available.
        bool allSuitOrPass = validActions.All(a =>
            a.Type is "bid_clubs" or "bid_diamonds" or "bid_hearts" or "bid_spades"
                    or "bid_pass" or "bid_alone");
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
                             ?? state.Metadata.GetValueOrDefault("bid_trump")
                             ?? GetDefinedTrump(state);
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

    /// <summary>
    /// The cards in the trick, the leader's first, so the led suit is the first card's. A
    /// trick is kept seat by seat; reading only the first trick zone saw seat 0's card alone,
    /// and the computer played every trick seat 0 had not yet played to as if leading it.
    /// </summary>
    private static List<Card> GetTrickCards(GameState state)
        => TrickPlays(state).Select(p => p.Card).ToList();

    /// <summary>Who has played what to the trick, the leader first.</summary>
    private static List<(string Owner, Card Card)> TrickPlays(GameState state)
    {
        var zones  = state.Zones.Values.Where(z => z.Type == "trick").ToList();
        var leader = state.Metadata.GetValueOrDefault("trick_leader");
        return zones.OrderBy(z => z.OwnerId == leader ? 0 : 1)
            .SelectMany(z => z.Cards.Select(c => (z.OwnerId ?? "", c)))
            .ToList();
    }

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

    /// <summary>
    /// Reads the fixed trump from the game definition's trick_taking phase.
    /// Used during bidding when trick_trump/bid_trump metadata isn't set yet.
    /// Returns null when trump is dynamic (e.g. "bid_result") or absent.
    /// </summary>
    private static string? GetDefinedTrump(GameState state)
    {
        var trickPhase = state.Definition.Phases.FirstOrDefault(p => p.Type == "trick_taking");
        if (trickPhase?.Extra?.TryGetValue("trump", out var el) == true
            && el.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var val = el.GetString();
            // Only return literal suit names — "bid_result" is dynamic.
            return val is "spades" or "hearts" or "diamonds" or "clubs" ? val : null;
        }
        return null;
    }
}
