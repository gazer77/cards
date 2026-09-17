using SkiaSharp;

namespace Cards.Rendering;

public enum CardFaceStyle
{
    /// <summary>Pip grids, face-card initials, small corner labels.</summary>
    Classic,

    /// <summary>Huge rank in the lower-right, large suit glyph in the upper-left.
    /// Easier to read on small phone screens.</summary>
    Simplified,
}

public interface ICardSkin
{
    string Id { get; }
    string DisplayName { get; }

    // Card back
    SKColor BackColor { get; }
    SKColor BackPatternColor { get; }
    SKColor BackBorderColor { get; }

    // Card face
    SKColor FaceColor { get; }
    SKColor FaceBorderColor { get; }
    SKColor RedSuitColor { get; }
    SKColor BlackSuitColor { get; }

    /// <summary>
    /// Face tint and border for a card the game treats as wild — 2s in Hand and Foot.
    /// Wildness is a rule, not a printed property, so the renderer says which cards
    /// are wild and the skin says what that looks like.
    /// </summary>
    SKColor WildFaceColor { get; }
    SKColor WildBorderColor { get; }

    // Geometry
    float CornerRadiusFraction { get; }  // fraction of card width, e.g. 0.08f

    // Rendering style
    CardFaceStyle FaceStyle { get; }
}
