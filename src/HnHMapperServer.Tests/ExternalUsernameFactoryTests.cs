using HnHMapperServer.Services.Services;

namespace HnHMapperServer.Tests;

/// <summary>
/// External display names (Steam personas, Discord usernames) must become valid mapper usernames
/// (^[a-zA-Z0-9_]{3,20}$) without ever colliding with an existing account.
/// </summary>
public class ExternalUsernameFactoryTests
{
    [Theory]
    [InlineData("jorb", "jorb")]
    [InlineData("Loftar the Great", "Loftar_the_Great")]
    [InlineData("  spaced   out  ", "spaced_out")]
    [InlineData("mr.dot-dash", "mr_dot_dash")]
    [InlineData("ünïcödé hearth", "ncd_hearth")]           // non-ASCII letters dropped, separators kept
    [InlineData("<script>alert(1)</script>", "scriptalert1script")]
    [InlineData("ab", "ab_")]                                 // padded to the minimum
    [InlineData("", "hearthling")]                            // nothing usable -> fallback
    [InlineData("!!!", "hearthling")]
    [InlineData("___leading_and_trailing___", "leading_and_trailing")]
    public void Sanitize_ProducesValidUsernames(string input, string expected)
    {
        var result = ExternalUsernameFactory.Sanitize(input);

        Assert.Equal(expected, result);
        Assert.Matches(@"^[a-zA-Z0-9_]{3,20}$", result);
    }

    [Fact]
    public void Sanitize_TruncatesLongNamesToTwentyCharacters()
    {
        var result = ExternalUsernameFactory.Sanitize("this_persona_name_is_far_too_long_for_us");

        Assert.Equal(20, result.Length);
        Assert.Matches(@"^[a-zA-Z0-9_]{3,20}$", result);
    }

    [Fact]
    public async Task MakeUnique_ReturnsBaseNameWhenFree()
    {
        var name = await ExternalUsernameFactory.MakeUniqueAsync("jorb", _ => Task.FromResult(false));

        Assert.Equal("jorb", name);
    }

    [Fact]
    public async Task MakeUnique_AppendsNumericSuffixUntilFree()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "jorb", "jorb_2", "jorb_3" };

        var name = await ExternalUsernameFactory.MakeUniqueAsync("jorb", c => Task.FromResult(taken.Contains(c)));

        Assert.Equal("jorb_4", name);
    }

    [Fact]
    public async Task MakeUnique_KeepsSuffixedNamesWithinTwentyCharacters()
    {
        var longName = "twentycharacterslong";   // exactly 20
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { longName };

        var name = await ExternalUsernameFactory.MakeUniqueAsync(longName, c => Task.FromResult(taken.Contains(c)));

        Assert.EndsWith("_2", name);
        Assert.True(name.Length <= 20);
        Assert.Matches(@"^[a-zA-Z0-9_]{3,20}$", name);
    }

    [Fact]
    public async Task MakeUnique_FallsBackToRandomSuffix_WhenSequentialOnesAreTaken()
    {
        var name = await ExternalUsernameFactory.MakeUniqueAsync("jorb",
            c => Task.FromResult(c == "jorb" || System.Text.RegularExpressions.Regex.IsMatch(c, @"^jorb_\d{1,2}$")));

        Assert.Matches(@"^jorb_\d{4}$", name);
    }

    [Fact]
    public async Task MakeUnique_GivesUpEventually_InsteadOfLoopingForever()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExternalUsernameFactory.MakeUniqueAsync("jorb", _ => Task.FromResult(true)));
    }
}
