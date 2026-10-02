using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Base class for game logic modules.  Replaces the manual switch-on-phase pattern
/// in <see cref="IGameLogic.Apply"/> / <see cref="IGameLogic.GetValidActions"/> with
/// a dictionary of <see cref="IPhaseHandler"/> objects, one per phase ID.
///
/// Subclasses call <see cref="RegisterPhase"/> during
/// <see cref="Initialize"/> to wire up their handlers.
/// The <c>game_over</c> phase is pre-registered with a no-op handler.
/// </summary>
public abstract class GameLogicBase : IGameLogic
{
    private readonly Dictionary<string, IPhaseHandler> _handlers = new();

    protected GameLogicBase()
    {
        RegisterPhase("game_over", GameOverPhaseHandler.Instance);
    }

    // ── Registration ──────────────────────────────────────────────────────────

    protected void RegisterPhase(string phaseId, IPhaseHandler handler)
        => _handlers[phaseId] = handler;

    /// <summary>
    /// Calls <see cref="IPhaseHandler.OnGameStart"/> on the handler registered
    /// for <paramref name="phaseId"/>.  Used by <see cref="DefaultGameLogic"/>
    /// to let the first-phase handler perform initialization (e.g. a custom deal).
    /// </summary>
    protected void CallOnGameStart(string phaseId, GameState state)
    {
        if (_handlers.TryGetValue(phaseId, out var h))
            h.OnGameStart(state);
    }

    // ── IGameLogic ────────────────────────────────────────────────────────────

    public abstract void Initialize(
        GameState state,
        int playerCount,
        IReadOnlyList<string> enabledHouseRules);


    /// <summary>
    /// Lets the handler for the current phase set up the moment the phase changes, and
    /// before any question is asked about it — in particular before a client asks whose
    /// turn it is, which is the first thing it asks.
    /// </summary>
    protected void EnterPhaseIfNeeded(GameState state)
    {
        if (state.EnteredPhase == state.CurrentPhaseId) return;
        state.EnteredPhase = state.CurrentPhaseId;

        if (_handlers.TryGetValue(state.CurrentPhaseId, out var handler))
            handler.OnPhaseEnter(state);
    }
    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        EnterPhaseIfNeeded(state);
        return _handlers.TryGetValue(state.CurrentPhaseId, out var h) ? h.GetValidActions(state) : [];
    }

    public void Apply(GameState state, GameAction action)
    {
        EnterPhaseIfNeeded(state);
        if (_handlers.TryGetValue(state.CurrentPhaseId, out var h))
            h.Apply(state, action);

        // An action usually is the phase change; entering the new one here means the
        // next question asked — whose turn is it? — is answered by a settled phase.
        EnterPhaseIfNeeded(state);
    }

    public virtual bool IsGameOver(GameState state)
        => state.CurrentPhaseId == "game_over";

    public virtual string GetStatusText(GameState state)
        => state.Metadata.GetValueOrDefault("status", "");

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        EnterPhaseIfNeeded(state);
        return _handlers.TryGetValue(state.CurrentPhaseId, out var h) ? h.GetSelectableCardIds(state) : [];
    }

    public IReadOnlyList<string> GetDropZoneIds(GameState state, string cardId)
    {
        EnterPhaseIfNeeded(state);
        return _handlers.TryGetValue(state.CurrentPhaseId, out var h) ? h.GetDropZoneIds(state, cardId) : [];
    }

    public GameAction? GetDefaultCardAction(GameState state, string cardId, int? uid)
    {
        EnterPhaseIfNeeded(state);
        return _handlers.TryGetValue(state.CurrentPhaseId, out var h) ? h.DefaultCardAction(state, cardId, uid) : null;
    }

    public bool SharedTableReady => _handlers.Values.All(h => h.SharedTableReady);

    /// <summary>
    /// Returns the auto-advance delay from the current phase handler, or 800 ms
    /// when the current player has a registered AI agent (giving the agent time to
    /// "think" before its action fires).
    /// </summary>
    public TimeSpan? GetAutoAdvanceDelay(GameState state)
    {
        EnterPhaseIfNeeded(state);

        if (_handlers.TryGetValue(state.CurrentPhaseId, out var h))
        {
            var d = h.GetAutoAdvanceDelay(state);
            if (d is not null) return d;
        }

        // Phase handler returned null — auto-advance only if an AI agent owns this turn.
        if (state.Players.Count > 0 &&
            state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id))
            return TimeSpan.FromMilliseconds(800);

        return null;
    }

    /// <summary>
    /// Chooses the action to fire during an auto-advance tick.
    /// When the current player has a registered agent:
    ///   • if selectable cards exist → agent picks a play_card action
    ///   • otherwise → agent picks from meaningful valid actions
    /// Falls back to the first valid action for scripted/automated phases.
    /// </summary>
    public GameAction GetAutoAction(GameState state)
    {
        var valid    = GetValidActions(state);
        var selCards = GetSelectableCardIds(state);

        if (!state.PlayerAgents.TryGetValue(state.CurrentPlayer.Id, out var agent))
            return valid.Count > 0 ? valid[0] : new GameAction("tap");

        // A card owed to the table (a conditional pickup's price) is the one forced
        // move in the game: discarding is refused until it is paid. An agent left to
        // tap cards here would try that refused discard forever — the table simply
        // stopped, mid-round, with the AI told "you must meld" and no way to.
        if (state.Metadata.ContainsKey("dd_must_meld") && valid.Any(a => a.Type == "meld"))
            return new GameAction("meld");

        if (selCards.Count > 0)
        {
            var cardActs = selCards
                .Select(id => new GameAction("play_card", CardId: id))
                .ToList<GameAction>();

            // Also include knock / gin / go_out so the agent can choose to end the hand
            // instead of always playing a card, and a lay the selection makes legal —
            // the agent picks its cards with select_card and presses meld itself.
            // (meld_done / confirm_pass are NOT included
            // here because they would be randomly selected before all melds/passes are done.)
            cardActs.AddRange(valid.Where(a => a.Type is "knock" or "gin" or "go_out" or "meld" or "add_to_meld"));

            var masked = GameStateMask.CreateViewFor(state, state.CurrentPlayer.Id);
            return agent.ChooseAction(masked, cardActs);
        }

        // Filter out purely internal/scripted action types.
        var meaningful = valid
            .Where(a => a.Type is not ("tap" or "ai_step"))
            .ToList();
        if (meaningful.Count > 0)
        {
            var masked = GameStateMask.CreateViewFor(state, state.CurrentPlayer.Id);
            return agent.ChooseAction(masked, meaningful);
        }

        return valid.Count > 0 ? valid[0] : new GameAction("tap");
    }

    // ── Dealer helpers ────────────────────────────────────────────────────────

    /// <summary>
    /// Assigns the initial dealer from the game definition's <c>rounds.first_dealer</c>:
    /// a random seat, or a deal for the deal (<see cref="DealForTheDeal"/>).
    /// </summary>
    protected static void AssignInitialDealer(GameState state)
    {
        if (state.Players.Count == 0) return;
        var rule = state.Definition.Rounds?.FirstDealer ?? new FirstDealerDefinition();

        var dealer = rule.Mode == "random"
            ? state.Players[state.Rng.Next(state.Players.Count)]
            : DealForTheDeal(state, rule);
        state.DealerId = dealer.Id;
    }

    /// <summary>
    /// Deals for the deal, as a table does: a fresh shuffled deck of the game's cards,
    /// one card face up to each player in turn from the first seat. <c>high_card</c> and
    /// <c>low_card</c> give everyone one and the highest (aces high) or lowest (aces low)
    /// deals, the tied dealing again; <c>deal_until</c> goes round until a card matches.
    ///
    /// Every card is written to the log, so the table can see how the dealer was chosen,
    /// and the dealer says so. The cards go back: the game's own deal is shuffled fresh.
    /// A role seat — Blackjack's dealer — is the house and takes no part.
    /// </summary>
    private static Player DealForTheDeal(GameState state, FirstDealerDefinition rule)
    {
        var seated = state.Players.Where(p => p.Role is null).ToList();
        if (seated.Count == 0) return state.Players[0];

        var deck = DeckBuilder.Build(state.Definition, seated.Count);
        DeckBuilder.Shuffle(deck, state.Rng);
        var wilds = MeldRules.WildRanks(state.Definition);
        int next  = 0;

        Card? Deal(Player p)
        {
            if (next >= deck.Count) return null;
            var card = deck[next++];
            GameText.Log(state, "log_dealt_for_deal", "{player} drew the {card}", p.Id,
                         ("card", GameText.CardName(card)));
            return card;
        }

        Player Chosen(Player p, Card card)
        {
            GameText.Announce(state, p.Id, "first_dealer", "{player} drew the {card} and deals first.",
                              ("card", GameText.CardName(card)));
            return p;
        }

        if (rule.Mode == "deal_until" && rule.DealUntil is { } match)
        {
            // Round and round until the card turns up. A deck with none of it — a match
            // that cannot be met — ends with the deck, and the first seat deals.
            for (int i = 0; next < deck.Count; i++)
            {
                var p = seated[i % seated.Count];
                if (Deal(p) is { } card && ZoneIntake.Matches(match, card, wilds)) return Chosen(p, card);
            }
            return seated[0];
        }

        // High or low card: everyone draws, and the tied draw again until one stands out.
        bool high = rule.Mode != "low_card";
        int Value(Card c) => c.Rank == Rank.Ace && !high ? 1 : (int)c.Rank;

        var drawing = seated;
        while (true)
        {
            var drawn = drawing.Select(p => (Player: p, Card: Deal(p))).ToList();
            if (drawn.Any(d => d.Card is null)) return drawing[0];   // out of cards: the first of them

            int best = high ? drawn.Max(d => Value(d.Card!)) : drawn.Min(d => Value(d.Card!));
            var tied = drawn.Where(d => Value(d.Card!) == best).ToList();
            if (tied.Count == 1) return Chosen(tied[0].Player, tied[0].Card!);
            drawing = tied.Select(d => d.Player).ToList();
        }
    }

    /// <summary>
    /// Rotates the dealer seat according to <c>rounds.dealer</c>.
    /// Stores the winning/losing player ID in <c>metadata["last_winner"]</c> and
    /// <c>metadata["last_loser"]</c> when those rotation modes are used.
    /// </summary>
    protected static void RotateDealer(GameState state)
    {
        if (state.Players.Count == 0) return;
        string mode = state.Definition.Rounds?.Dealer ?? "rotates_left";

        int current = state.DealerId is null ? 0
            : state.Players.FindIndex(p => p.Id == state.DealerId);
        if (current < 0) current = 0;

        int n = state.Players.Count;
        int next = mode switch
        {
            // "left" = clockwise in card-game convention = index−1 (south→west→north→east)
            "rotates_right"  => (current + 1) % n,
            "winner"         => FindPlayerIndex(state, "last_winner", current),
            "loser"          => FindPlayerIndex(state, "last_loser",  current),
            "alternates"     => (current - 1 + n) % n,
            _                => (current - 1 + n) % n,  // rotates_left
        };

        state.DealerId = state.Players[next].Id;
    }

    private static int FindPlayerIndex(GameState state, string metaKey, int fallback)
    {
        if (!state.Metadata.TryGetValue(metaKey, out var id)) return fallback;
        int idx = state.Players.FindIndex(p => p.Id == id);
        return idx < 0 ? fallback : idx;
    }

    // ── Standard handlers ─────────────────────────────────────────────────────

    /// <summary>Terminal phase — no valid actions, no auto-advance.</summary>
    private sealed class GameOverPhaseHandler : IPhaseHandler
    {
        public static readonly GameOverPhaseHandler Instance = new();
        public IReadOnlyList<GameAction> GetValidActions(GameState _) => [];
        public void Apply(GameState state, GameAction action) { }
    }
}
