namespace Cards.Engine;

public enum Suit { Clubs, Diamonds, Hearts, Spades }

public enum Rank
{
    Two = 2, Three, Four, Five, Six, Seven, Eight, Nine, Ten,
    Jack, Queen, King, Ace
}

public class Card
{
    public Suit Suit { get; }
    public Rank Rank { get; }
    public bool IsFaceUp { get; set; }
    public bool IsWild { get; set; }

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
