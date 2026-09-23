using Cards.Models;
using Cards.Engine;
using Cards.Rendering;
using Cards.Services;
using SkiaSharp;

namespace Cards.Tests;

/// <summary>
/// The player's own size for what the table draws, and the card-points overlay.
///
/// The sizes have to default to what the table drew before they existed — a setting
/// nobody has touched that changes the look is a regression wearing an option's hat.
/// </summary>
public sealed class UiSizeTests
{
    private static SettingsService Fresh() => new(new InMemorySettingsStore());

    [Fact]
    public void Everything_starts_at_the_size_the_table_already_drew()
    {
        var settings = Fresh();

        foreach (var (target, _) in UiSizes.Targets)
        {
            Assert.Equal(UiSizes.Default, settings.GetUiSize(target));
            Assert.Equal(1.0, settings.UiScale(target), 4);
        }
        Assert.Equal(UiSizes.Default, settings.CommonUiSize);
        Assert.False(settings.ShowCardValues);
    }

    [Fact]
    public void Each_step_down_is_a_quarter_smaller()
    {
        for (int i = 1; i < UiSizes.All.Length; i++)
            Assert.Equal(UiSizes.All[i - 1].Scale * 0.75, UiSizes.All[i].Scale, 3);
    }

    [Fact]
    public void One_element_may_be_sized_without_the_others()
    {
        var settings = Fresh();
        settings.SetUiSize("cards", "s");

        Assert.Equal("s", settings.GetUiSize("cards"));
        Assert.Equal(UiSizes.Default, settings.GetUiSize("bubbles"));

        // "All" shows no tick while the elements disagree — the menu says "Mixed".
        Assert.Null(settings.CommonUiSize);

        settings.SetAllUiSizes("m");
        Assert.Equal("m", settings.CommonUiSize);
        Assert.Equal(UiSizes.ScaleOf("m"), settings.UiScale("bubbles"), 4);
    }

    [Fact]
    public void A_smaller_card_size_draws_smaller_cards_in_the_same_place()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var definition = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var state = new GameState { GameId = definition.Id, Definition = definition, Rng = new SeededRandomSource(4) };
        LogicRegistry.Create(definition).Initialize(state, 2, []);
        var canvas = new SKImageInfo(1000, 800);

        var full  = ZoneLayoutEngine.Compute(state, canvas);
        var small = ZoneLayoutEngine.Compute(state, canvas, (float)UiSizes.ScaleOf("s"));

        foreach (var (a, b) in full.Zip(small))
        {
            Assert.Equal(a.Bounds, b.Bounds);                       // the zone keeps its place
            Assert.Equal(a.CardWidth * UiSizes.ScaleOf("s"), b.CardWidth, 2);
        }
    }

    [Fact]
    public void Card_points_are_offered_only_by_games_that_score_cards()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var golf   = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var hearts = loader.LoadAsync("hearts").GetAwaiter().GetResult()!;

        Assert.True(ScoringEngine.HasCardValues(golf));
        Assert.Equal(0, ScoringEngine.CardPointValue(golf, [new Card(Suit.Spades, Rank.King)]));
        Assert.Equal(5, ScoringEngine.CardPointValue(golf, [new Card(Suit.Spades, Rank.Five)]));

        // Hearts scores the cards it names in a list form CardPointValue does not read;
        // writing 0 on every card there would be a confident wrong answer.
        Assert.False(ScoringEngine.HasCardValues(hearts));
    }
}

/// <summary>
/// The card name bubble a tap raises: it is sized like everything else, and it is
/// where the card's worth is said — the question "what is this card?" and "what is it
/// worth?" are the same question in a game that counts cards.
/// </summary>
public sealed class CardTooltipTests
{
    [Fact]
    public void The_name_bubble_is_one_of_the_sizable_elements()
        => Assert.Contains(UiSizes.Targets, t => t.Id == "tooltip");

    [Fact]
    public void A_card_reads_with_its_value_in_a_game_that_scores_cards()
    {
        var loader = new GameLoader(new EmbeddedGameAssetSource());
        var golf   = loader.LoadAsync("golf").GetAwaiter().GetResult()!;
        var jack   = new Card(Suit.Clubs, Rank.Jack);

        Assert.Equal("Jack of Clubs", jack.DisplayName);
        Assert.Equal(10, ScoringEngine.CardPointValue(golf, [jack]));
    }
}

/// <summary>
/// What the name bubble says a card is worth, which the game decides rather than the
/// player: a King is nothing in Golf and ten in Hand and Foot, and neither is written
/// on the card.
/// </summary>
public sealed class CardValueNameTests
{
    private static GameDefinition Load(string id)
        => new GameLoader(new EmbeddedGameAssetSource()).LoadAsync(id).GetAwaiter().GetResult()!;

    [Theory]
    [InlineData("golf")]
    [InlineData("gin-rummy")]
    [InlineData("hand-and-foot")]
    public void A_game_that_scores_its_cards_says_so_in_its_definition(string id)
    {
        var definition = Load(id);
        Assert.True(ScoringEngine.HasCardValues(definition));
        Assert.True(definition.Ui?.ShowCardValues, $"{id} scores its cards but does not say to name the value.");
    }

    [Fact]
    public void A_game_that_scores_no_cards_has_nothing_to_say()
    {
        // Left unset, and nothing to show even if it were set: the bubble falls back to
        // the card's name alone rather than writing 0 on everything.
        var hearts = Load("hearts");
        Assert.Null(hearts.Ui?.ShowCardValues);
        Assert.False(ScoringEngine.HasCardValues(hearts));
    }

    [Fact]
    public void The_value_is_the_games_own_and_not_the_cards_face()
    {
        // The case that makes this worth showing at all: a King reads as nothing in one
        // game and as ten in another, with the same face both times.
        var king = new Card(Suit.Spades, Rank.King);
        Assert.Equal(0,  ScoringEngine.CardPointValue(Load("golf"), [king]));
        Assert.Equal(10, ScoringEngine.CardPointValue(Load("hand-and-foot"), [king]));
    }
}
