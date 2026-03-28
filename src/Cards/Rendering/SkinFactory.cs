namespace Cards.Rendering;

public static class SkinFactory
{
    public static ICardSkin Create(string id) => id switch
    {
        "simple"  => new SimpleCardSkin(),
        _         => new DefaultCardSkin(), // "classic"
    };
}
