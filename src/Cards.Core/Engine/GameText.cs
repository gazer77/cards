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
        var text = state.Definition?.Text;

        // The person at this screen is seat 0. A _you variant, declared or default,
        // addresses them directly.
        bool you = forPlayerId is not null
                && state.Players.Count > 0 && state.Players[0].Id == forPlayerId;

        string template =
            (you ? Lookup(text?.Messages, key + "_you") : null)
            ?? Lookup(text?.Messages, key)
            ?? (you ? DefaultYou.GetValueOrDefault(key) : null)
            ?? fallback;

        return Fill(state, template, forPlayerId, values);
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
        ["turn_draw"]           = "{player}'s turn — Draw a card",
        ["turn_discard"]        = "{player}'s turn — Discard a card",
        ["turn_swap"]           = "{player}'s turn — Tap a card to swap, or discard the drawn card",
        ["turn_owed"]           = "{player}'s turn — Meld the {card} they took",
        ["foot_picked_up"]      = "{player} picked up their foot!",
        ["must_meld_first"]     = "The {card} taken must be melded before discarding.",
        ["rank_unmeldable"]     = "{rank}s cannot be melded.",
        ["not_a_meld"]          = "That is not a meld — pick three or more of a rank.",
        ["opening_too_low"]     = "{player}'s first meld this round must be worth {required}; that is {offered}.",
        ["melds_laid"]          = "{count} melds laid!",
        ["meld_laid"]           = "Meld laid!",
        ["added_to_meld"]       = "Added to meld.",
        ["add_one_rank"]        = "Pick cards of one rank to add to a meld.",
        ["no_meld_of_rank"]     = "No meld of {rank}s on the table — lay it as a new meld.",
        ["too_many_wilds"]      = "That would leave the meld more wild than real.",
        ["no_meld_takes_wilds"] = "No meld can take that many wilds.",
        ["card_filed"]          = "{player}: {card} to {zone}",
    };

    public static readonly IReadOnlyDictionary<string, string> ActionKeys = new Dictionary<string, string>
    {
        ["draw_from_{zone}"] = "Draw from {Zone}",
        ["gin"]              = "Gin!",
        ["knock"]            = "Knock",
        ["go_out"]           = "Go Out",
        ["meld"]             = "Lay Meld",
        ["add_to_meld"]      = "Add to Meld",
        ["discard"]          = "Discard",
        ["clear_selection"]  = "Clear",
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
        ["turn_owed"]        = "Your turn — Meld the {card} you took",
        ["foot_picked_up"]   = "You picked up your foot!",
        ["opening_too_low"]  = "Your first meld this round must be worth {required}; that is {offered}.",
        ["must_meld_first"]  = "You must meld the {card} you took before discarding.",
    };
}
