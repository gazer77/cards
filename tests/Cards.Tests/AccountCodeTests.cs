using Cards.Services;

namespace Cards.Tests;

/// <summary>An account's code: six plain words, typed however a person types them, and unguessable.</summary>
public sealed class AccountCodeTests
{
    [Fact]
    public void A_code_is_six_words_from_the_list()
    {
        for (int i = 0; i < 50; i++)
        {
            var words = AccountCode.New().Split(' ');
            Assert.Equal(AccountCode.Words, words.Length);
            Assert.All(words, w => Assert.Contains(w, AccountCode.WordList));
        }
    }

    [Theory]
    [InlineData("Amber Otter Maple River Lamp Cocoa")]
    [InlineData("amber-otter-maple-river-lamp-cocoa")]
    [InlineData("  amber otter, maple. river  lamp\ncocoa ")]
    public void A_code_is_read_however_it_is_typed(string typed)
        => Assert.Equal("amber otter maple river lamp cocoa", AccountCode.Normalize(typed));

    [Theory]
    [InlineData("amber otter maple river lamp")]              // five words
    [InlineData("amber otter maple river lamp cocoa jam")]    // seven
    [InlineData("amber otter maple river lamp cocao")]        // a typo
    [InlineData("")]
    public void A_code_that_is_not_one_is_caught_before_it_is_tried(string typed)
        => Assert.Null(AccountCode.Normalize(typed));

    [Fact]
    public void The_word_list_is_plain_distinct_and_long_enough_to_be_unguessable()
    {
        var list = AccountCode.WordList;
        Assert.Equal(list.Count, list.Distinct().Count());
        Assert.All(list, w => Assert.Matches("^[a-z]{2,8}$", w));

        // Six words from the list: 2^50 and more, against a server allowing 60 tries a minute.
        double bits = AccountCode.Words * Math.Log2(list.Count);
        Assert.True(bits >= 50, $"Only {bits:F1} bits.");
    }

    [Fact]
    public void A_codes_hash_is_the_same_for_the_same_code_and_says_nothing_of_it()
    {
        string code = "amber otter maple river lamp cocoa";
        Assert.Equal(AccountCode.Hash(code), AccountCode.Hash(code));
        Assert.NotEqual(AccountCode.Hash(code), AccountCode.Hash("amber otter maple river lamp jam"));
        Assert.DoesNotContain("amber", AccountCode.Hash(code), StringComparison.OrdinalIgnoreCase);
    }
}
