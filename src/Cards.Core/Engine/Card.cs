namespace Cards.Engine;

public enum Suit { Clubs, Diamonds, Hearts, Spades }

public enum Rank
{
    // Joker = 0 sorts below all pip cards and is detected via IsWild.
    Joker = 0,
    Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten,
    Jack, Queen, King, Ace
}

public class Card
{
    public Suit Suit { get; }
    public Rank Rank { get; }
    public bool IsFaceUp { get; set; }
    public bool IsWild { get; set; }

    /// <summary>
    /// Identifies this physical card, as distinct from what it is.
    ///
    /// <see cref="Id"/> describes a card — rank and suit — and two cards from a
    /// multi-deck game share it. That is correct for the rules, where any five of
    /// hearts is as good as another, and wrong for anything tracking a card as an
    /// object: the renderer keys hit-testing and every animation off it, so with
    /// duplicates a tap can move a different card and a fly-in can land on the wrong
    /// one. Masked multiplayer needs it too, since a hidden card has no rank or suit
    /// to name it by.
    ///
    /// Assigned in <see cref="DeckBuilder"/> in build order, so the same definition
    /// yields the same uids on every client and a shuffle only reorders them. Zero
    /// means "not from a built deck" — a hypothetical card used to evaluate a hand,
    /// which is never drawn.
    /// </summary>
    public int Uid { get; set; }

    public Card(Suit suit, Rank rank, bool isFaceUp = false)
    {
        Suit = suit;
        Rank = rank;
        IsFaceUp = isFaceUp;
    }

    public string Id
    {
        get
        {
            if (Rank == Rank.Joker)
            {
                // Two Jokers distinguished by suit: JKRc = "black" joker, JKRr = "red" joker.
                char jSuit = IsRed ? 'r' : 'k';
                return $"JKR{jSuit}";
            }
            string rank = Rank switch
            {
                Rank.Jack  => "J",
                Rank.Queen => "Q",
                Rank.King  => "K",
                Rank.Ace   => "A",
                _          => ((int)Rank).ToString()
            };
            char suit = Suit.ToString().ToLower()[0];
            return $"{rank}{suit}";
        }
    }

    public string DisplayName
    {
        get
        {
            if (Rank == Rank.Joker) return IsRed ? "Red Joker" : "Black Joker";
            string rank = Rank switch
            {
                Rank.Jack  => "Jack",
                Rank.Queen => "Queen",
                Rank.King  => "King",
                Rank.Ace   => "Ace",
                _          => ((int)Rank).ToString()
            };
            return $"{rank} of {Suit}";
        }
    }

    public bool IsRed => Suit is Suit.Hearts or Suit.Diamonds;

    public override string ToString() => Id;
}
