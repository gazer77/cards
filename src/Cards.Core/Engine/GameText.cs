using Cards.Models;

namespace Cards.Engine;

/// <summary>
/// Every word the table says, resolved through the definition.
///
/// A handler names what it wants to say by key and supplies the default wording; the
/// definition's <c>text</c> block overrides any key. So a shipped game reads as it
/// always did, a definition that wants different words writes them, and the code holds
/// no sentence the definition cannot replace — which is the rule: the game lives in the
/// definition, only the computer players in code.
///
/// Placeholders in a template are filled from the values a call passes:
/// <c>{player}</c>, <c>{card}</c>, <c>{rank}</c>, <c>{count}</c>, <c>{required}</c>,
/// <c>{offered}</c>, <c>{zone}</c>. A key may have a <c>_you</c> variant, used when
/// the player concerned is the one at this screen: "Your turn" rather than
/// "Player 1's turn".
/// </summary>
public static class GameText
{
    /// <summary>A message about the position, filled in.</summary>
    public static string Message(
        GameState state, string key, string fallback,
        string? forPlayerId = null, params (string Name, object? Value)[] values)
    {
        // A _you variant, declared or default, addresses the player concerned directly.
        // The line is stored as seat 0 reads it, and the other wording is kept beside
        // it so any seat can be shown the line in its own words.
        string neutral = Fill(state, Template(state, key, fallback, you: false), forPlayerId, values);
        if (forPlayerId is null) return neutral;

        string addressed = Fill(state, Template(state, key, fallback, you: true), forPlayerId, values);
        if (addressed == neutral) return neutral;

        return Remember(state, neutral, [(forPlayerId, addressed)]);
    }

    private static string Template(GameState state, string key, string fallback, bool you)
    {
        var text = state.Definition?.Text;
        return (you ? Lookup(text?.Messages, key + "_you") : null)
            ?? Lookup(text?.Messages, key)
            ?? (you ? DefaultYou.GetValueOrDefault(key) : null)
            ?? fallback;
    }

    /// <summary>
    /// Stores a line that reads differently by seat: returns seat 0's reading and
    /// records every other one.
    /// </summary>
    private static string Remember(GameState state, string neutral, IEnumerable<(string Viewer, string Text)> addressed)
    {
        var variant = new TextVariant { Neutral = neutral };
        foreach (var (viewer, text) in addressed)
            if (text != neutral) variant.ByViewer[viewer] = text;
        if (variant.ByViewer.Count == 0) return neutral;

        string seat0 = state.Players.Count > 0 && variant.ByViewer.TryGetValue(state.Players[0].Id, out var mine)
            ? mine : neutral;
        state.TextVariants[seat0] = variant;
        return seat0;
    }

    /// <summary>
    /// A line built from pieces that each depend on who is reading — a score summary
    /// that calls one team "Your team". <paramref name="build"/> is given the reader's
    /// seat, or null for someone it addresses as nobody; the result is seat 0's reading
    /// and every seat's is remembered.
    /// </summary>
    public static string PerViewer(GameState state, Func<string?, string> build)
        => Remember(state, build(null), state.Players.Select(p => (p.Id, build(p.Id))).ToList());

    /// <summary>
    /// A stored line as <paramref name="viewerId"/> reads it. Lines that read the same
    /// for everyone come back unchanged.
    /// </summary>
    public static string Render(GameState state, string text, string? viewerId)
    {
        if (!state.TextVariants.TryGetValue(text, out var v)) return text;
        return viewerId is not null && v.ByViewer.TryGetValue(viewerId, out var mine) ? mine : v.Neutral;
    }

    /// <summary>
    /// A message about a team, addressed to the person at this screen when the team is
    /// theirs. {team} fills with the team's name.
    /// </summary>
    public static string TeamMessage(GameState state, string key, string fallback, Team team,
        params (string Name, object? Value)[] values)
    {
        string neutral = Fill(state, Template(state, key, fallback, you: false).Replace("{team}", team.Name), null, values);
        string ours    = Fill(state, Template(state, key, fallback, you: true).Replace("{team}", team.Name), null, values);
        return Remember(state, neutral, team.PlayerIds.Select(id => (id, ours)).ToList());
    }


    /// <summary>
    /// Records something that happened, in the words the definition chose.
    ///
    /// The log was a history of the status line and nothing else, so it read as a list
    /// of whose turn it was. What a player wants from it is what moved: who drew, who
    /// took what. A card is named only where the table could see it — a card off the
    /// deck goes into the log as "drew a card", because writing its name would be the
    /// log telling everyone something the game had not.
    /// </summary>
    public static void Log(
        GameState state, string key, string fallback,
        string? forPlayerId = null, params (string Name, object? Value)[] values)
        => state.GameLog.Add(Message(state, key, fallback, forPlayerId, values));

    /// <summary>
    /// Records something a player did, and has them say it at the table.
    ///
    /// The log keeps the line; the bubble beside their seat is the saying. Used where a
    /// choice is not visible in the cards — naming trump changes every card on the
    /// table and moves none of them, so without this it happened silently and the
    /// status line mentioned it afterwards as a footnote.
    /// </summary>
    public static void Announce(
        GameState state, string playerId, string key, string fallback,
        params (string Name, object? Value)[] values)
    {
        string line = Message(state, key, fallback, playerId, values);
        state.GameLog.Add(line);
        state.Announcements.Add((playerId, line));
    }

    /// <summary>The label for an action button.</summary>
    public static string Action(GameState state, string key, string fallback,
        params (string Name, object? Value)[] values)
        => Fill(state, Lookup(state.Definition?.Text?.Actions, key) ?? fallback, null, values);

    /// <summary>A card the way a player says it: "Ace of Spades", "Joker".</summary>
    public static string CardName(Card card)
        => card.Rank == Rank.Joker ? "Joker" : $"{MeldRules.RankDisplayName(card.Rank)} of {card.Suit}";

    private static string? Lookup(Dictionary<string, string>? table, string key)
        => table is not null && table.TryGetValue(key, out var t) ? t : null;

    private static string Fill(
        GameState state, string template, string? forPlayerId, (string Name, object? Value)[] values)
    {
        string result = template;

        if (forPlayerId is not null && result.Contains("{player}"))
        {
            var player = state.Players.FirstOrDefault(p => p.Id == forPlayerId);
            result = result.Replace("{player}", player?.Name ?? forPlayerId);
        }

        foreach (var (name, value) in values)
            result = result.Replace("{" + name + "}", value?.ToString() ?? "");

        return result;
    }

    /// <summary>
    /// Every key a definition may override, with its default wording — the vocabulary the
    /// validator checks against, and what the schema documents. A key here is a promise
    /// that some handler says it.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> MessageKeys = new Dictionary<string, string>
    {
        ["add_one_rank"] = "Pick cards of one rank to add to a meld.",
        ["added_to_meld"] = "Added to meld.",
        ["ask_confirm"] = "Ask {opponent} for {rank}?",
        ["ask_hit"] = "Got {count} {rank} from {opponent}!{books} Go again.",
        ["blackjack_hand"] = "You: {value}  |  Dealer shows: {dealer}",
        ["blackjack_split_hand"] = "{player}: second hand {value}  |  Dealer shows: {dealer}",
        ["bj_continue"] = "Tap to continue.",
        ["bj_dealer_had"] = "Dealer: {value}",
        ["bj_doubled"] = "{player} doubled down",
        ["bj_result_bust"] = "{player}: bust −{amount}",
        ["bj_result_lose"] = "{player}: {value}, dealer wins −{amount}",
        ["bj_result_natural"] = "{player}: Blackjack! +{amount}",
        ["bj_result_push"] = "{player}: push",
        ["bj_result_surrender"] = "{player} surrendered — {amount} back",
        ["bj_result_win"] = "{player}: {value} wins +{amount}",
        ["bj_split"] = "{player} split their {rank}s",
        ["bj_surrendered"] = "{player} surrendered",
        ["book_complete"] = "1 book complete!",
        ["books_complete"] = "{count} books complete!",
        ["books_tally"] = "(Books — You: {mine} | {opponent}: {theirs})",
        ["card_filed"] = "{player}: {card} to {zone}",
        ["dealer_done"] = "Dealer: {soft}{value} — Tap to see result.",
        ["dealer_drawing"] = "Dealer: {soft}{value} — Tap for next card.",
        ["fish_deck_empty"] = "Go Fish! The deck is empty. {opponent}'s turn.",
        ["fish_drew"] = "Go Fish! You drew {rank}.{books} {opponent}'s turn.",
        ["fish_lucky"] = "Go Fish! Lucky — you drew {rank}.{books} Go again!",
        ["foot_picked_up"] = "{player} picked up their foot!",
        ["game_drawn"] = "It's a tie!",
        ["game_ended"] = "Game ended.",
        ["game_started"] = "Game started!",
        ["game_won"] = "{player} wins!",
        ["game_won_team"] = "{team} wins!",
        ["meld_laid"] = "Meld laid!",
        ["melds_laid"] = "{count} melds laid!",
        ["must_meld_first"] = "The {card} taken must be melded before discarding.",
        ["no_cards_drew"] = "You had no cards — drew one from the deck.",
        ["no_cards_no_deck"] = "You have no cards and the deck is empty. {opponent}'s turn.",
        ["no_meld_of_rank"] = "No meld of {rank}s on the table — lay it as a new meld.",
        ["no_meld_takes_wilds"] = "No meld can take that many wilds.",
        ["not_a_meld"] = "That is not a meld — pick three or more of a rank.",
        ["keep_a_discard"] = "Keep a card to discard — {player} cannot go out yet.",
        ["opening_too_low"] = "{player}'s first meld this round must be worth {required}; that is {offered}.",
        ["opponent_ask_hit"] = "{opponent} asked for {rank} — got {count}!{books} {opponent} goes again…",
        ["opponent_fish"] = "{opponent} asked for {rank} — Go Fish.{books} Your turn!",
        ["opponent_fish_deck_empty"] = "{opponent} asked for {rank} — Go Fish! Deck is empty. Your turn!",
        ["opponent_fish_lucky"] = "{opponent} asked for {rank} — Go Fish, but drew one!{books} {opponent} goes again…",
        ["opponent_no_cards_drew"] = "{opponent} has no cards — drew from deck. Your turn!",
        ["opponent_no_cards_no_deck"] = "{opponent} has no cards and the deck is empty. Your turn!",
        ["pass_more"] = "Passing {direction}: Select {count} more cards",
        ["pass_none"] = "No passing this round. Tap to continue.",
        ["pass_one_more"] = "Passing {direction}: Select 1 more card",
        ["pass_ready"] = "Passing {direction}: Tap 'Pass Cards' to confirm",
        ["pass_select"] = "Select {count} cards to pass {direction}.",
        ["pot_won_by_fold"] = "Everyone folded. {player} wins the pot ({pot}).",
        ["rank_unmeldable"] = "{rank}s cannot be melded.",
        ["dealer_discarded"] = "Dealer discarded. Let's play!",
        ["debt_forgiven"] = "{player} could not meld the {card} they took",
        ["log_drew"] = "{player} drew a card",
        ["log_named_trump"] = "{player} named {suit} trump",
        ["log_ordered_up"] = "{player} ordered up {suit}",
        ["log_played"] = "{player} played the {card}",
        ["log_played_hidden"] = "{player} played a card",
        ["log_swapped"] = "{player} played the {card} and discarded the {discarded}",
        ["log_took_card"] = "{player} took the {card}",
        ["log_took_pile"] = "{player} took the pile — {count} cards",
        ["log_took_up"] = "{player} took up the {card}",
        ["log_discarded"] = "{player} discarded a card",
        ["log_discarded_card"] = "{player} discarded the {card}",
        ["reveal_more"] = "{player}'s turn — Tap {count} cards to turn over",
        ["reveal_ready"] = "{player}'s turn — Press Flip to turn {count} over",
        ["reveal_one"] = "{player}'s turn — Tap a card to turn over",
        ["round_started"] = "Round {round}",
        ["showdown_won"] = "{player} wins with {hand}: {cards}",
        ["too_many_wilds"] = "That would leave the meld more wild than real.",
        ["trick_won"] = "{player} wins the trick.",
        ["turn"] = "{player}'s turn",
        ["turn_ask"] = "Tap a card to ask for its rank.{books}",
        ["turn_bet"] = "{player}'s turn  |  Pot: {pot}  |  To call: {to_call}",
        ["turn_bid"] = "{player}'s bid",
        ["turn_bid_high"] = "{player}'s bid — current high: {high}",
        ["turn_discard"] = "{player}'s turn — Discard a card",
        ["turn_draw"] = "{player}'s turn — Draw a card",
        ["turn_meld"] = "{player}'s turn — Select cards to meld or tap Done.",
        ["turn_owed"] = "{player}'s turn — Meld the {card} they took",
        ["turn_swap"] = "{player}'s turn — Tap a card to swap, or discard the drawn card",
        ["turn_flip"] = "{player}'s turn — Tap a face-down card to turn over",
        ["turn_flip_ready"] = "{player}'s turn — Press Flip to turn it over",
        ["turn_trump"] = "{player}'s turn  |  Trump: {trump}",
        ["war_flip"] = "Tap to flip!",
    };

    public static readonly IReadOnlyDictionary<string, string> ActionKeys = new Dictionary<string, string>
    {
        ["draw_from_{zone}"] = "Draw from {Zone}",
        ["add_to_meld"] = "Add to Meld",
        ["all_in"] = "All-In",
        ["ask"] = "Ask for {rank}",
        ["bid_accept"] = "Order Up",
        ["bid_alone"] = "Go Alone",
        ["bid_pass"] = "Pass",
        ["call"] = "Call {amount}",
        ["check"] = "Check",
        ["clear_selection"] = "Clear",
        ["confirm_pass"] = "Pass Cards",
        ["continue"] = "Continue",
        ["deselect"] = "Cancel",
        ["discard"] = "Discard",
        ["double_down"] = "Double",
        ["end_game"] = "End Game",
        ["end_turn"] = "End Turn",
        ["discard_drawn"] = "Discard Drawn",
        ["flip"] = "Flip",
        ["fold"] = "Fold",
        ["gin"] = "Gin!",
        ["go_out"] = "Go Out",
        ["hit"] = "Hit",
        ["knock"] = "Knock",
        ["lay_meld"] = "Lay Meld",
        ["lay_off"] = "Lay Off",
        ["meld"] = "Lay Meld",
        ["meld_done"] = "Done",
        ["raise"] = "Raise",
        ["ready"] = "✓ Ready",
        ["split"] = "Split",
        ["stand"] = "Stand",
        ["surrender"] = "Surrender",
    };

    /// <summary>Whether a definition may override this message key (a _you variant counts).</summary>
    public static bool IsMessageKey(string key)
        => MessageKeys.ContainsKey(key) || (key.EndsWith("_you") && MessageKeys.ContainsKey(key[..^4]));

    /// <summary>Whether a definition may override this action key; draw_from_* is open-ended.</summary>
    public static bool IsActionKey(string key)
        => ActionKeys.ContainsKey(key) || key.StartsWith("draw_from_");

    /// <summary>
    /// Second-person defaults for messages that name a player, so the default table
    /// reads naturally without every definition declaring both forms.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultYou = new()
    {
        ["turn_draw"]        = "Your turn — Draw a card",
        ["turn_discard"]     = "Your turn — Discard a card",
        ["turn_swap"]        = "Your turn — Tap a card to swap, or discard the drawn card",
        ["turn_flip"]        = "Your turn — Tap a face-down card to turn over",
        ["turn_owed"]        = "Your turn — Meld the {card} you took",
        ["foot_picked_up"]   = "You picked up your foot!",
        ["keep_a_discard"]   = "Keep a card to discard — you cannot go out yet.",
        ["opening_too_low"]  = "Your first meld this round must be worth {required}; that is {offered}.",
        ["must_meld_first"]  = "You must meld the {card} you took before discarding.",
        ["game_won"]         = "You win!",
        ["game_won_team"]    = "Your team wins! ({team})",
        ["turn"]             = "Your turn",
        ["turn_trump"]       = "Your turn  |  Trump: {trump}",
        ["turn_bet"]         = "Your turn  |  Pot: {pot}  |  To call: {to_call}",
        ["turn_meld"]        = "Your turn — Select cards to meld or tap Done.",
        ["turn_bid"]         = "Your bid",
        ["turn_bid_high"]    = "Your bid — current high: {high}",
        ["trick_won"]        = "You win the trick!",
        ["pot_won_by_fold"]  = "Everyone folded. You win the pot ({pot})!",
        ["showdown_won"]     = "You win with {hand}: {cards}",
        ["reveal_one"]       = "Tap a card to turn over",
        ["reveal_more"]      = "Tap {count} cards to turn over",
        ["debt_forgiven"]      = "You could not meld the {card} you took",
        ["log_drew"]           = "You drew a card",
        ["blackjack_split_hand"] = "Your second hand: {value}  |  Dealer shows: {dealer}",
        ["bj_doubled"]         = "You doubled down",
        ["bj_result_bust"]     = "You bust −{amount}",
        ["bj_result_lose"]     = "You: {value}, dealer wins −{amount}",
        ["bj_result_natural"]  = "Blackjack! +{amount}",
        ["bj_result_push"]     = "You push",
        ["bj_result_surrender"] = "You surrendered — {amount} back",
        ["bj_result_win"]      = "You: {value} wins +{amount}",
        ["bj_split"]           = "You split your {rank}s",
        ["bj_surrendered"]     = "You surrendered",
        ["log_named_trump"]    = "You named {suit} trump",
        ["log_ordered_up"]     = "You ordered up {suit}",
        ["log_played"]         = "You played the {card}",
        ["log_played_hidden"]  = "You played a card",
        ["log_swapped"]        = "You played the {card} and discarded the {discarded}",
        ["log_took_card"]      = "You took the {card}",
        ["log_took_pile"]      = "You took the pile — {count} cards",
        ["log_took_up"]        = "You took up the {card}",
        ["log_discarded"]      = "You discarded a card",
        ["log_discarded_card"] = "You discarded the {card}",
        ["reveal_ready"]     = "Press Flip to turn {count} over",
        ["turn_flip_ready"]  = "Press Flip to turn it over",
    };
}
