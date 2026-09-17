namespace Cards.Engine;

/// <summary>A player action that can be applied to a GameState.</summary>
/// <remarks>
/// <see cref="CardId"/> names a card description ("4h") — which, in a multi-deck game,
/// several cards answer to. <see cref="CardUid"/> names one physical card. A tap on the
/// table carries both; an agent that only knows descriptions sends the id alone and the
/// engine resolves it to a copy.
/// </remarks>
public record GameAction(
    string  Type,
    string? PlayerId = null,
    string? ZoneId   = null,
    string? CardId   = null,
    string? Label    = null,
    int?    CardUid  = null);
