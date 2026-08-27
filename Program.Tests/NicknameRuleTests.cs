using Minecraft;

namespace Minecraft.Tests;

/// <summary>
/// A player's name is not only a label: commands are built out of it, and the
/// parser that reads them takes an unquoted name as [A-Za-z0-9_.+-] and stops
/// at the first character outside that. A Cyrillic nickname therefore cannot be
/// addressed at all - "/tp @s Женя" does not run, and neither does a message,
/// a team, a scoreboard entry or any mod command that names a player. Two
/// players on The Broken Script Enhanced found this out by not being able to
/// teleport to each other.
///
/// So a new name is held to the rule Minecraft itself holds names to, and one
/// already saved is left exactly as it is: it is what that player is called on
/// every world they have played, and this is not the place to take it away.
/// </summary>
public sealed class NicknameRuleTests
{
    [Theory]
    [InlineData("MarkZamore")]
    [InlineData("anuvenn")]
    [InlineData("ASS_in")]
    [InlineData("Bob")]
    [InlineData("0123456789abcdef")]
    public void AName_TheGameCanAddress_IsAccepted(string name)
    {
        Assert.True(LocalIdentityService.TryNormalizeNewNickname(name, out var normalized, out var error));
        Assert.Equal(name, normalized);
        Assert.Empty(error);
    }

    /// <summary>And the ones it cannot, with a reason a player can act on.</summary>
    [Theory]
    [InlineData("Женя")]
    [InlineData("Mark Zamore")]
    [InlineData("Ник🙂")]
    [InlineData("Ab")]
    [InlineData("seventeen_letters!")]
    public void AName_TheGameCannotAddress_IsRefusedWithAReason(string name)
    {
        Assert.False(LocalIdentityService.TryNormalizeNewNickname(name, out _, out var error));
        Assert.NotEmpty(error);
    }

    /// <summary>
    /// The field refuses the letter as it is typed, the way the memory box
    /// refuses anything but digits - a name half-typed is short, and shortness
    /// alone is not yet a mistake.
    /// </summary>
    [Fact]
    public void TheFieldTakes_OnlyLettersTheGameCanAddress()
    {
        Assert.True(LocalIdentityService.IsNicknameDraftValid("M"));
        Assert.True(LocalIdentityService.IsNicknameDraftValid("Mark_1"));
        Assert.False(LocalIdentityService.IsNicknameDraftValid("Ж"));
        Assert.False(LocalIdentityService.IsNicknameDraftValid("Mark Zamore"));
        Assert.False(LocalIdentityService.IsNicknameDraftValid("seventeen_letters"));
    }

    /// <summary>
    /// A name that is already stored keeps working: the loader normalises it as
    /// it always did, and nothing here renames anybody.
    /// </summary>
    [Fact]
    public void ANameAlreadySaved_IsLeftExactlyAsItIs()
    {
        Assert.Equal("Женя", LocalIdentityService.NormalizeNickname("Женя", "Fallback"));
        Assert.True(LocalIdentityService.TryNormalizeNickname("Женя", out var normalized, out _));
        Assert.Equal("Женя", normalized);
    }
}
