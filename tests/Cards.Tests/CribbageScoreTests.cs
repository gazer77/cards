using Cards.Engine;

namespace Cards.Tests;

/// <summary>
/// Cribbage's counting against hands every player knows the score of. If the arithmetic
/// is wrong, everything built on it is wrong in a way that looks plausible on the table.
/// </summary>
public sealed class CribbageScoreTests
{
    /// <summary>"5h", "Js", "10c", "Ad" — rank then suit.</summary>
    private static Card C(string s)
    {
        var suit = s[^1] switch { 'h' => Suit.Hearts, 'd' => Suit.Diamonds, 'c' => Suit.Clubs, _ => Suit.Spades };
        var rank = s[..^1] switch
        {
            "A" => Rank.Ace, "J" => Rank.Jack, "Q" => Rank.Queen, "K" => Rank.King,
            var n => (Rank)int.Parse(n),
        };
        return new Card(suit, rank, isFaceUp: true);
    }

    private static List<Card> Cards(params string[] s) => s.Select(C).ToList();

    [Theory]
    [InlineData("5h 5c 5d Js", "5s", false, 29)]   // the perfect hand
    [InlineData("4c 5d 5h 6s", "6c", false, 24)]   // double-double run with fifteens
    [InlineData("2c 4d 6h 8s", "Kc", false, 0)]    // "nineteen"
    [InlineData("7c 8d 9h 9s", "Kc", false, 10)]   // double run of three (6) + fifteen 7-8 (2) + pair (2)
    [InlineData("Ac 2d 3h 4s", "5c", false, 7)]    // a run of five, one fifteen
    [InlineData("2h 4h 6h 8h", "Kc", false, 4)]    // a hand's four-card flush
    [InlineData("2h 4h 6h 8h", "Kc", true, 0)]     // a crib needs all five
    [InlineData("2h 4h 6h 8h", "Kh", true, 5)]     // which it has here
    [InlineData("Jd 2c 4s 6h", "3d", false, 8)]    // nobs (1) + run 2-3-4 (3) + fifteens J-2-3 and 6-4-3-2 (4)
    public void A_hand_counts_what_a_player_would_count(string hand, string starter, bool crib, int expected)
    {
        var points = CribbageScore.Show(Cards(hand.Split(' ')), C(starter), crib);
        Assert.Equal(expected, points.Total);
    }

    [Fact]
    public void His_nobs_is_the_jack_of_the_starters_suit()
    {
        Assert.Equal(1, CribbageScore.Show(Cards("Jd", "2c", "7s", "9h"), C("3d"), false).Nobs);
        Assert.Equal(0, CribbageScore.Show(Cards("Jd", "2c", "7s", "9h"), C("3c"), false).Nobs);
    }

    [Theory]
    [InlineData("5h Kd", 2, 0)]            // fifteen for two
    [InlineData("4h 5d 6c", 5, 3)]         // fifteen and a run of three
    [InlineData("7h 7d", 2, 0)]            // a pair
    [InlineData("7h 7d 7c", 6, 0)]         // pair royal
    [InlineData("3h 3d 3c 3s", 12, 0)]     // double pair royal
    [InlineData("3h 2d 4c", 3, 3)]         // a run in any order
    [InlineData("3h 2d 4c 2s", 0, 0)]      // broken by the repeat: no run ends on the second two
    [InlineData("Kh Qd 9c", 0, 0)]         // twenty-nine, nothing
    [InlineData("Kh Qd 6c 5s", 2, 0)]      // thirty-one for two
    public void A_card_played_scores_what_it_makes(string sequence, int points, int run)
    {
        var peg = CribbageScore.Peg(Cards(sequence.Split(' ')));
        Assert.Equal(points, peg.Total);
        Assert.Equal(run, peg.RunLength);
    }
}
