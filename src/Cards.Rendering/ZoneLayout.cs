using SkiaSharp;
using Cards.Engine;

namespace Cards.Rendering;

public enum ZoneRenderHint
{
    Fan,        // hand: overlapping cards, face-up or face-down
    Stack,      // deck/pile: stacked cards showing depth, back or top face
    Spread,     // community/trick: all cards visible, minimal overlap
    CountOnly,  // won tricks: card back stack + numeric count label
    Empty,      // no cards — draw a placeholder slot
}

public sealed record ZoneLayout(
    Zone Zone,
    SKRect Bounds,
    float CardWidth,
    float CardHeight,
    ZoneRenderHint Hint,
    bool FaceUp,
    float RotationDegrees,    // 0, 90, 180, or 270 for side players
    string? Label,
    bool IsCurrentPlayer,
    /// <summary>
    /// Quarter turns from the bottom seat to the seat this zone faces: 0 bottom, 1 right,
    /// 2 top, 3 left. Decorations declared relative to the cards ("bottom" = toward the
    /// player) are turned by this, so a label under my melds is over the opponent's.
    /// </summary>
    int SeatQuarterTurns = 0
);
