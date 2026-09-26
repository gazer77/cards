using System.Text.Json;
using Cards.Logic;
using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Phase handler for Go Fish, two to six players, any of them people.
///
/// Every seat takes the same turn: pick a rank you hold, ask someone for it, take every
/// card of that rank they have — or go fish from the deck. Getting what you asked for,
/// or drawing it, earns another ask. A person picks by tapping a card and pressing
/// "Ask Ana for Kings"; a computer seat picks through <see cref="GoFishAiAgent"/>.
///
/// It was written as one person against the computer: seat 0 asked through the table
/// and seat 1 ran separate turn logic of its own, so a second person at the table would
/// never have been asked anything. Now the seat is only who is asking.
///
/// Phase definition parameters:
///   book_size    — number of matching cards to form a book (default 4).
///   collect_to   — zone-id prefix for book piles (default "books").
///
/// State metadata:
///   selected_rank     — rank code the asking person tapped ("A","2"…"K")
///   selected_card     — the card they tapped
///   gf_known:{seat}   — ranks the table has seen that seat hold (they asked for them)
///   gf_denied:{seat}  — ranks that seat has said it does not hold
/// Both are what anyone at a real table could remember, so every seat may read them.
/// </summary>
public sealed class GoFishHandler : IPhaseHandler
{
    private readonly int    _bookSize;
    private readonly string _collectTo;

    public GoFishHandler(PhaseDefinition def, string nextPhaseId)
    {
        _bookSize  = GetInt(def, "book_size")    ?? 4;
        _collectTo = GetString(def, "collect_to") ?? "books";
    }

    // ── IPhaseHandler ─────────────────────────────────────────────────────────

    public void OnGameStart(GameState state)
    {
        // Computer seats ask the way a Go Fish player does, not the way a trick-taker
        // plays. A seat the engine gave a general agent gets this one instead.
        foreach (var id in state.PlayerAgents.Keys.ToList())
            state.PlayerAgents[id] = new GoFishAiAgent(id);

        foreach (var p in state.Players)
            CheckBooks(state, p.Id);

        SetTurnStatus(state);
    }

    public IReadOnlyList<GameAction> GetValidActions(GameState state)
    {
        var seat = state.CurrentPlayer.Id;
        var hand = Hand(state, seat);

        // Nothing in hand: draw one, or pass when the deck is gone too. No choice in
        // either, so the table does it.
        if (hand.Count == 0) return [new GameAction("refill")];

        if (state.PlayerAgents.ContainsKey(seat)) return [new GameAction("ai_step")];

        string? sel = state.Metadata.GetValueOrDefault("selected_rank");
        if (sel is null) return [];

        string rankName = RankPlural(RankFromCode(sel));
        var actions = Targets(state, seat)
            .Select(t => new GameAction("ask", ZoneId: $"hand:{t.Id}",
                Label: GameText.Action(state, "ask", "Ask {target} for {rank}", ("target", t.Name), ("rank", rankName))))
            .ToList();
        actions.Add(new GameAction("deselect", Label: GameText.Action(state, "deselect", "Cancel")));
        return actions;
    }

    public void Apply(GameState state, GameAction action)
    {
        var seat = state.CurrentPlayer.Id;

        switch (action.Type)
        {
            case "select_card" when action.CardId is { } id && !state.PlayerAgents.ContainsKey(seat):
                Select(state, seat, id);
                break;

            case "deselect":
                Deselect(state);
                SetTurnStatus(state);
                break;

            case "ask" when state.Metadata.GetValueOrDefault("selected_rank") is { } rank:
            {
                var target = TargetFromZone(state, seat, action.ZoneId);
                Deselect(state);
                if (target is not null) Ask(state, seat, target.Id, rank);
                break;
            }

            case "ai_step" when state.PlayerAgents.ContainsKey(seat):
                ComputerAsk(state, seat);
                break;

            case "refill":
                Refill(state, seat);
                break;
        }
    }

    public TimeSpan? GetAutoAdvanceDelay(GameState state)
    {
        if (Hand(state, state.CurrentPlayer.Id).Count == 0) return TimeSpan.FromMilliseconds(800);
        if (state.PlayerAgents.ContainsKey(state.CurrentPlayer.Id)) return TimeSpan.FromMilliseconds(1200);
        return null;
    }

    public IReadOnlyList<string> GetSelectableCardIds(GameState state)
    {
        var seat = state.CurrentPlayer.Id;
        if (state.PlayerAgents.ContainsKey(seat)) return [];
        return Hand(state, seat).Cards.Select(c => c.Id).ToList();
    }

    // ── A person's turn ───────────────────────────────────────────────────────

    private static void Select(GameState state, string seat, string cardId)
    {
        if (!Hand(state, seat).Cards.Any(c => c.Id == cardId)) return;

        string rankCode = RankCode(cardId);
        state.Metadata["selected_rank"] = rankCode;
        state.Metadata["selected_card"] = cardId;

        // The rank is the asker's until they ask; everyone else only sees them thinking.
        state.Metadata["status"] = GameText.Message(state, "gf_choosing", "{player} is choosing what to ask for.",
            seat, ("rank", RankPlural(RankFromCode(rankCode))));
    }

    private static void Deselect(GameState state)
    {
        state.Metadata.Remove("selected_rank");
        state.Metadata.Remove("selected_card");
    }

    /// <summary>Who may be asked: everyone else still holding cards — or, if nobody is, everyone else.</summary>
    private static List<Player> Targets(GameState state, string seat)
    {
        var others = state.Players.Where(p => p.Id != seat && p.Role is null).ToList();
        var holding = others.Where(p => Hand(state, p.Id).Count > 0).ToList();
        return holding.Count > 0 ? holding : others;
    }

    private static Player? TargetFromZone(GameState state, string seat, string? zoneId)
    {
        var targets = Targets(state, seat);
        return targets.FirstOrDefault(t => $"hand:{t.Id}" == zoneId) ?? (zoneId is null ? targets.FirstOrDefault() : null);
    }

    // ── A computer's turn ─────────────────────────────────────────────────────

    private void ComputerAsk(GameState state, string seat)
    {
        var masked = GameStateMask.CreateViewFor(state, seat);
        var choice = GoFishAiAgent.Choose(masked, seat);

        var target = TargetFromZone(state, seat, choice.ZoneId) ?? Targets(state, seat).First();
        Ask(state, seat, target.Id, RankCode(choice.CardId!));
    }

    // ── The ask ───────────────────────────────────────────────────────────────

    private void Ask(GameState state, string asker, string target, string rankCode)
    {
        var askerHand  = Hand(state, asker);
        var targetHand = Hand(state, target);
        var deck       = state.Zones["deck"];
        string rankName = RankPlural(RankFromCode(rankCode));

        // Asking says you hold the rank, to everyone.
        Remember(state, $"gf_known:{asker}", rankCode);

        var matching = targetHand.Cards.Where(c => RankCode(c.Id) == rankCode).ToList();
        if (matching.Count > 0)
        {
            foreach (var c in matching)
            {
                targetHand.Remove(c);
                c.IsFaceUp = true;
                askerHand.Add(c);
            }
            // They had them and now they do not.
            Forget(state, $"gf_known:{target}", rankCode);
            Remember(state, $"gf_denied:{target}", rankCode);

            string books = BooksNote(state, CheckBooks(state, asker));
            state.Metadata["status"] = GameText.Between(state, "gf_hit",
                "{player} asked {target} for {rank} and got {count}.{books} {player} goes again.",
                asker, target, ("rank", rankName), ("count", matching.Count), ("books", books));
            FinishAsk(state, goAgain: true);
            return;
        }

        Forget(state, $"gf_known:{target}", rankCode);
        Remember(state, $"gf_denied:{target}", rankCode);

        if (deck.Count == 0)
        {
            state.Metadata["status"] = GameText.Between(state, "gf_fish_empty",
                "{player} asked {target} for {rank} — Go Fish. The deck is empty.",
                asker, target, ("rank", rankName));
            FinishAsk(state, goAgain: false);
            return;
        }

        var drawn = deck.Draw()!;
        drawn.IsFaceUp = true;
        askerHand.Add(drawn);

        // The asker took an unseen card, so one thing they said they lacked may be wrong now.
        ExpireOldest(state, $"gf_denied:{asker}");

        bool lucky = RankCode(drawn.Id) == rankCode;
        string note = BooksNote(state, CheckBooks(state, asker));

        state.Metadata["status"] = lucky
            ? GameText.Between(state, "gf_lucky",
                "{player} asked {target} for {rank} — Go Fish, and drew one!{books} {player} goes again.",
                asker, target, ("rank", rankName), ("books", note))
            : GameText.Between(state, "gf_fish",
                "{player} asked {target} for {rank} — Go Fish.{books}",
                asker, target, ("rank", rankName), ("books", note),
                ("drawn", RankPlural(RankFromCode(RankCode(drawn.Id)))));
        FinishAsk(state, goAgain: lucky);
    }

    private void Refill(GameState state, string seat)
    {
        var deck = state.Zones["deck"];
        if (deck.Count > 0)
        {
            var drawn = deck.Draw()!;
            drawn.IsFaceUp = true;
            Hand(state, seat).Add(drawn);
            ExpireOldest(state, $"gf_denied:{seat}");
            CheckBooks(state, seat);
            state.Metadata["status"] = GameText.Message(state, "gf_refill", "{player} had no cards and drew one.", seat);
            FinishAsk(state, goAgain: Hand(state, seat).Count > 0);
            return;
        }

        state.Metadata["status"] = GameText.Message(state, "gf_out", "{player} has no cards left.", seat);
        FinishAsk(state, goAgain: false);
    }

    /// <summary>Ends the game if it is over; otherwise the same seat asks again, or the next one.</summary>
    private static void FinishAsk(GameState state, bool goAgain)
    {
        if (CheckWinCondition(state)) return;
        if (!goAgain) state.AdvancePlayer();
    }

    private static void SetTurnStatus(GameState state)
        => state.Metadata["status"] = GameText.Message(state, "gf_turn", "{player} to ask.", state.CurrentPlayer.Id);

    // ── Books ─────────────────────────────────────────────────────────────────

    private int CheckBooks(GameState state, string playerId)
    {
        var hand  = Hand(state, playerId);
        var books = state.FindZone($"{_collectTo}:{playerId}");

        var groups = hand.Cards
            .GroupBy(c => RankCode(c.Id))
            .Where(g => g.Count() >= _bookSize)
            .ToList();

        foreach (var group in groups)
        {
            foreach (var c in group.Take(_bookSize).ToList())
            {
                hand.Remove(c);
                c.IsFaceUp = true;
                books?.Add(c);
            }
            state.AddScore(playerId, 1);
            Forget(state, $"gf_known:{playerId}", group.Key);
        }

        return groups.Count;
    }

    /// <summary>" 2 books complete!" or "" — the plural is the definition's, one key per form.</summary>
    private static string BooksNote(GameState state, int books) => books switch
    {
        0 => "",
        1 => " " + GameText.Message(state, "book_complete", "1 book complete!"),
        _ => " " + GameText.Message(state, "books_complete", "{count} books complete!", values: ("count", books)),
    };

    // ── The end ───────────────────────────────────────────────────────────────

    private static bool CheckWinCondition(GameState state)
    {
        if (state.CurrentPhaseId == "game_over") return true;

        var result = WinConditionEngine.Instance.Check(state);
        if (result is null) return false;

        state.Metadata["status"]      = result.StatusMessage;
        state.Metadata["sub"]         = result.SubMessage ?? "";
        state.Metadata["last_winner"] = result.WinnerId ?? "";
        state.CurrentPhaseId          = "game_over";
        return true;
    }

    // ── What the table remembers ──────────────────────────────────────────────

    internal static HashSet<string> Recall(GameState state, string key)
    {
        var val = state.Metadata.GetValueOrDefault(key, "");
        return string.IsNullOrEmpty(val) ? [] : [.. val.Split(',')];
    }

    private static void Remember(GameState state, string key, string rankCode)
    {
        var list = state.Metadata.GetValueOrDefault(key, "");
        var ranks = string.IsNullOrEmpty(list) ? new List<string>() : list.Split(',').ToList();
        ranks.Remove(rankCode);
        ranks.Add(rankCode);   // newest last, so the oldest is the first to expire
        state.Metadata[key] = string.Join(',', ranks);

        // Seeing a seat hold a rank outranks having heard it had none, and vice versa.
        string opposite = key.StartsWith("gf_known:") ? "gf_denied:" + key[9..] : "gf_known:" + key[10..];
        Forget(state, opposite, rankCode);
    }

    private static void Forget(GameState state, string key, string rankCode)
    {
        var ranks = Recall(state, key);
        if (!ranks.Remove(rankCode)) return;
        var ordered = state.Metadata[key].Split(',').Where(r => r != rankCode).ToList();
        if (ordered.Count == 0) state.Metadata.Remove(key);
        else                    state.Metadata[key] = string.Join(',', ordered);
    }

    /// <summary>
    /// Retires the oldest "does not hold" for a seat that has just taken an unseen card.
    ///
    /// One card can restore at most one ruled-out rank, so forgetting everything on each
    /// draw is far too much — it sends the computer straight back to the question it
    /// just had refused. Forgetting nothing is the opposite mistake: a rank the seat
    /// draws would be ruled out for good. Oldest-first tracks the real uncertainty.
    /// </summary>
    private static void ExpireOldest(GameState state, string key)
    {
        var val = state.Metadata.GetValueOrDefault(key, "");
        if (string.IsNullOrEmpty(val)) return;

        var ranks = val.Split(',').Skip(1).ToList();
        if (ranks.Count == 0) state.Metadata.Remove(key);
        else                  state.Metadata[key] = string.Join(',', ranks);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static Zone Hand(GameState state, string playerId) => state.Zones[$"hand:{playerId}"];

    internal static string RankCode(string cardId) => cardId[..^1];

    internal static Rank RankFromCode(string code) => code switch
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
