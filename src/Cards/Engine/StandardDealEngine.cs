namespace Cards.Engine;

/// <summary>
/// Default deal implementation.  Reads <see cref="Cards.Models.DealDefinition"/> from the
/// game definition and deals cards:
///
///   • Clockwise (player 0 → 1 → … → N-1), one card per player per round, for
///     <c>cards_per_player</c> rounds — unless <c>anim_deal_steps</c> is present,
///     in which case that explicit multi-pass sequence is used instead.
///   • <c>face</c> controls individual card orientation:
///       "up"    — all cards face-up
///       "down"  — all cards face-down  (default when omitted)
///       "owner" — face-up in zones whose visibility is "all" or "owner";
///                 face-down otherwise
///   • <c>pattern</c> — group-size deal, e.g. <c>"3-2"</c>: deal 3 cards to every
///     player clockwise, then 2 cards to every player clockwise.  Takes precedence
///     over <c>cards_per_player</c> when present.
///   • <c>remainder_to</c> — after dealing, any undealt cards are moved to this
///     zone.  Omit (or set to "deck") to leave the remainder in the deck zone.
///   • <c>then_flip_top_to</c> — after dealing (and remainder move), flip the
///     top deck card face-up into this zone (e.g. to seed a discard pile).
///   • <c>anim_delay_ms</c> — milliseconds between each individual card in the
///     animation (default 130).
///
/// For games whose initial deal cannot be expressed as a uniform clockwise deal
/// (e.g. Blackjack's mixed face-up/face-down sequence), the logic class should
/// perform the deal itself and call <see cref="RecordResult"/> to store a
/// <see cref="DealResult"/> on the state for the animation layer.
/// </summary>
public sealed class StandardDealEngine : IDealStrategy
{
    public static readonly StandardDealEngine Instance = new();

    // ── IDealStrategy ────────────────────────────────────────────────────────

    public DealResult Deal(GameState state, int playerCount, IReadOnlyList<string> enabledRules)
        => DealInternal(state, playerCount, enabledRules, cardsPerPlayerOverride: null);

    /// <summary>
    /// Overload that lets the calling logic class supply a <paramref name="cardsPerPlayerOverride"/>
    /// when house rules or game-specific logic need to deviate from the JSON definition.
    /// </summary>
    public DealResult Deal(GameState state, int playerCount, IReadOnlyList<string> enabledRules,
        int cardsPerPlayerOverride)
        => DealInternal(state, playerCount, enabledRules, cardsPerPlayerOverride);

    // ── Core implementation ───────────────────────────────────────────────────

    private static DealResult DealInternal(
        GameState state, int playerCount, IReadOnlyList<string> enabledRules,
        int? cardsPerPlayerOverride)
    {
        var def      = state.Definition.Deal;
        var deckZone = state.FindZone("deck")
            ?? throw new InvalidOperationException(
                $"Game '{state.GameId}' requires a 'deck' zone before calling StandardDealEngine.");

        // Build and shuffle into the deck zone.
        var cards = DeckBuilder.Build(state.Definition.DeckType);
        DeckBuilder.Shuffle(cards);
        foreach (var c in cards) deckZone.Add(c);

        int animDelayMs = def?.AnimDelayMs ?? 130;

        var steps        = new List<(int PlayerIndex, int Count)>();
        var byPlayer     = new Dictionary<int, List<string>>();
        for (int i = 0; i < playerCount; i++) byPlayer[i] = [];

        if (def?.StacksPerPlayer > 0)
            DealStacks(state, playerCount, def.StacksPerPlayer, def.CardsPerStack, def.Face, deckZone, steps, byPlayer);
        else if (def?.AnimDealSteps is { Count: > 0 } animSteps)
            DealByExplicitSteps(state, playerCount, animSteps, def.Face, deckZone, steps, byPlayer);
        else if (def?.Pattern is { Length: > 0 } pattern)
            DealByPattern(state, playerCount, pattern, def.Face, deckZone, steps, byPlayer);
        else
        {
            int cardsPerPlayer = cardsPerPlayerOverride
                ?? def?.GetCardsPerPlayer(playerCount)
                ?? 5;
            DealClockwise(state, playerCount, cardsPerPlayer, def?.Face, deckZone, steps, byPlayer);
        }

        // Move undealt cards out of the deck if requested.
        if (def?.RemainderTo is { Length: > 0 } remTo && remTo != "deck")
        {
            var dest = state.FindZone(remTo);
            if (dest is not null)
                while (!deckZone.IsEmpty)
                    dest.Add(deckZone.Draw()!);
        }

        // Flip top deck card to a named zone (e.g. seed a discard pile).
        if (def?.ThenFlipTopTo is { Length: > 0 } flipTo)
        {
            var dest = state.FindZone(flipTo);
            if (dest is not null && !deckZone.IsEmpty)
            {
                var card = deckZone.Draw()!;
                card.IsFaceUp = true;
                dest.Add(card);
            }
        }

        // Flip the top card of a specific zone face-up in place (e.g. Euchre kitty turn-up).
        if (def?.ThenFlipTopOf is { Length: > 0 } flipOfZone)
        {
            var zone = state.FindZone(flipOfZone);
            if (zone?.TopCard is { } top)
                top.IsFaceUp = true;
        }

        // Apply peek_count: flip N cards face-up in each player's grid zone after dealing.
        // Cards are already in random order (deck was shuffled) so we flip the first N.
        foreach (var zoneDef in state.Definition.Zones.Where(z => z.PeekCount > 0))
        {
            if (zoneDef.Owner == "each_player")
            {
                foreach (var p in state.Players)
                {
                    var zone = state.FindZone($"{zoneDef.Id}:{p.Id}");
                    if (zone is null) continue;
                    int count = Math.Min(zoneDef.PeekCount, zone.Count);
                    for (int i = 0; i < count; i++)
                        zone.Cards[i].IsFaceUp = true;
                }
            }
            else
            {
                var zone = state.FindZone(zoneDef.Id);
                if (zone is not null)
                {
                    int count = Math.Min(zoneDef.PeekCount, zone.Count);
                    for (int i = 0; i < count; i++)
                        zone.Cards[i].IsFaceUp = true;
                }
            }
        }

        return RecordResult(state, byPlayer, steps, animDelayMs);
    }

    // ── Deal strategies ───────────────────────────────────────────────────────

    /// <summary>
    /// Hand-and-Foot style: deal <paramref name="stackCount"/> stacks of
    /// <paramref name="cardsPerStack"/> cards to each player.
    /// Stack 0 → hand:{p}, stack 1 → foot:{p}, stack 2+ → hand2:{p}, etc.
    /// Cards in each stack are dealt one-at-a-time round-robin across all players
    /// so the animation mirrors a real deal.
    /// </summary>
    private static void DealStacks(
        GameState state, int playerCount, int stackCount, int cardsPerStack, string? face,
        Zone deckZone, List<(int, int)> steps, Dictionary<int, List<string>> byPlayer)
    {
        string[] stackZoneNames = ["hand", "foot", "hand2", "hand3"];
        for (int stack = 0; stack < stackCount; stack++)
        {
            string zoneSuffix = stack < stackZoneNames.Length ? stackZoneNames[stack] : $"hand{stack}";
            for (int card = 0; card < cardsPerStack; card++)
            {
                for (int pIdx = 0; pIdx < playerCount; pIdx++)
                {
                    if (deckZone.IsEmpty) return;
                    var pid  = state.Players[pIdx].Id;
                    var zone = state.FindZone($"{zoneSuffix}:{pid}") ?? state.FindZone(zoneSuffix);
                    if (zone is null) continue;
                    var c = deckZone.Draw()!;
                    c.IsFaceUp = FaceUp(face, zone);
                    zone.Add(c);
                    byPlayer[pIdx].Add(c.Id);
                    steps.Add((pIdx, 1));
                }
            }
        }
    }

    private static void DealClockwise(
        GameState state, int playerCount, int cardsPerPlayer, string? face,
        Zone deckZone, List<(int, int)> steps, Dictionary<int, List<string>> byPlayer)
    {
        for (int round = 0; round < cardsPerPlayer; round++)
        {
            for (int pIdx = 0; pIdx < playerCount; pIdx++)
            {
                if (deckZone.IsEmpty) return;
                var zone = PlayerHandZone(state, pIdx);
                if (zone is null) continue;

                var card = deckZone.Draw()!;
                card.IsFaceUp = FaceUp(face, zone);
                zone.Add(card);
                byPlayer[pIdx].Add(card.Id);
                steps.Add((pIdx, 1));
            }
        }
    }

    private static void DealByExplicitSteps(
        GameState state, int playerCount, List<int[]> animSteps, string? face,
        Zone deckZone, List<(int, int)> steps, Dictionary<int, List<string>> byPlayer)
    {
        foreach (var s in animSteps)
        {
            int pIdx = s[0], count = s[1];
            if (pIdx >= playerCount || deckZone.IsEmpty) continue;

            var zone = PlayerHandZone(state, pIdx);
            if (zone is null) continue;

            steps.Add((pIdx, count));
            for (int c = 0; c < count && !deckZone.IsEmpty; c++)
            {
                var card = deckZone.Draw()!;
                card.IsFaceUp = FaceUp(face, zone);
                zone.Add(card);
                byPlayer[pIdx].Add(card.Id);
            }
        }
    }

    private static void DealByPattern(
        GameState state, int playerCount, string pattern, string? face,
        Zone deckZone, List<(int, int)> steps, Dictionary<int, List<string>> byPlayer)
    {
        // Parse "3-2" → [3, 2].  Each number = cards dealt to every player in one clockwise pass.
        var batches = pattern.Split('-', StringSplitOptions.RemoveEmptyEntries)
                             .Select(s => int.TryParse(s, out int n) ? n : 0)
                             .Where(n => n > 0)
                             .ToList();

        foreach (int batchSize in batches)
        {
            for (int pIdx = 0; pIdx < playerCount; pIdx++)
            {
                if (deckZone.IsEmpty) return;
                var zone = PlayerHandZone(state, pIdx);
                if (zone is null) continue;

                steps.Add((pIdx, batchSize));
                for (int c = 0; c < batchSize && !deckZone.IsEmpty; c++)
                {
                    var card = deckZone.Draw()!;
                    card.IsFaceUp = FaceUp(face, zone);
                    zone.Add(card);
                    byPlayer[pIdx].Add(card.Id);
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Stores a <see cref="DealResult"/> on <paramref name="state"/> and returns it.
    /// Call this from custom deal logic (e.g. BlackjackLogic) that doesn't use
    /// <see cref="Deal"/> but still wants the animation layer to consume the result.
    /// </summary>
    public static DealResult RecordResult(
        GameState state,
        Dictionary<int, List<string>> byPlayer,
        IReadOnlyList<(int PlayerIndex, int Count)> steps,
        int animDelayMs = 130)
    {
        var result = new DealResult
        {
            CardsByPlayerIndex = byPlayer,
            Steps              = steps,
            AnimDelayMs        = animDelayMs,
        };
        state.LastDealResult = result;
        return result;
    }

    private static Zone? PlayerHandZone(GameState state, int playerIndex)
    {
        if (playerIndex >= state.Players.Count) return null;
        var pid    = state.Players[playerIndex].Id;
        string tgt = state.Definition.Deal?.TargetZone ?? "hand";
        return state.FindZone($"{tgt}:{pid}")
            ?? state.FindZone(tgt)
            ?? state.FindZone($"hand:{pid}")
            ?? state.FindZone("hand");
    }

    private static bool FaceUp(string? face, Zone zone) => face switch
    {
        "up"    => true,
        "owner" => zone.Visibility is "all" or "owner",
        _       => false,  // "down" or null
    };
}
