using SkiaSharp;

namespace Cards.Rendering;

public interface ITableTheme
{
    string Id { get; }
    string DisplayName { get; }

    SKColor FeltColor { get; }
    SKColor FeltEdgeColor { get; }
    SKColor ZoneOutlineColor { get; }
    SKColor ZoneLabelColor { get; }
    SKColor PlayerNameColor { get; }
    SKColor CurrentPlayerHighlight { get; }
}
