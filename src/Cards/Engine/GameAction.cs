namespace Cards.Engine;

/// <summary>A player action that can be applied to a GameState.</summary>
public record GameAction(
    string  Type,
    string? PlayerId = null,
    string? ZoneId   = null,
    string? CardId   = null,
    string? Label    = null);
