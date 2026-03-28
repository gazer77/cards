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

    // Geometry
    float CornerRadiusFraction { get; }  // fraction of card width, e.g. 0.08f

    // Rendering style
    CardFaceStyle FaceStyle { get; }
}
