using System.IO;
using Xunit;

namespace Minecraft.Tests;

/// <summary>
/// A launcher that has just been downloaded knows no name, and says so.
///
/// It used to invent one: an empty name became the Windows account name, and
/// failing that the literal "Player". Nobody chose either, and yet that is the
/// name a friend then sees on a shared world - which is how somebody ends up
/// playing as "PC". The name is now the player's to give, and Steam's to offer
/// only if the player has not given one by the time Steam answers.
/// </summary>
public sealed class FirstRunNicknameTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AFreshInstall_IsGivenNoNameAtAll(string? stored)
    {
        Assert.Equal(string.Empty, LocalIdentityService.NormalizedOrNothing(stored));
    }

    [Theory]
    // The plain case, and the one this exists for: a persona with a space in it
    // is a perfectly good name once the space is gone.
    [InlineData("Shizoid", "Shizoid")]
    [InlineData("Mark Zamore", "MarkZamore")]
    [InlineData("anuvenn 💀", "anuvenn")]
    [InlineData("ASS_in", "ASS_in")]
    // Steam allows far more than sixteen characters; the game does not.
    [InlineData("a_very_long_persona_name", "a_very_long_pers")]
    public void APersona_BecomesANameTheGameCanAddress(string persona, string expected)
    {
        var name = LocalIdentityService.NicknameFromPersona(persona);

        Assert.Equal(expected, name);
        Assert.True(
            LocalIdentityService.IsNameMinecraftAccepts(name),
            $"\"{name}\" came out of a persona but the game could not address it.");
        // It also has to be a name the player can then edit in place: the field
        // refuses any draft with a letter outside the rule.
        Assert.True(LocalIdentityService.IsNicknameDraftValid(name));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Женя")]
    [InlineData("🙂🙂🙂")]
    [InlineData("...")]
    // Two letters is a name Minecraft will not have, and half a name is worse
    // than none: an empty field asks to be filled, a stump does not.
    [InlineData("Ab")]
    [InlineData("PC")]
    public void APersonaWithNothingUsable_IsNotTakenAtAll(string? persona)
    {
        Assert.Equal(string.Empty, LocalIdentityService.NicknameFromPersona(persona));
    }

    /// <summary>
    /// Nothing in the launcher may name a player after the computer they sit
    /// at. This is the whole of that rule, and it is one line to check.
    /// </summary>
    [Fact]
    public void NothingNamesAPlayerAfterTheirComputer()
    {
        var offenders = Directory
            .EnumerateFiles(FindRepositoryDirectory("Program"), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains("Environment.UserName", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These invent a name out of the Windows account: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The three moments the rule is made of, pinned where they are written:
    /// a stored name is kept, an empty one asks Steam, and a nameless launcher
    /// will not start a game.
    /// </summary>
    [Fact]
    public void TheRuleIsWiredWhereSteamAnswers()
    {
        var window = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var settling = Between(window, "private void ResolveAndPersistLocalIdentity(", "\n    }");

        // A stored name is normalised and kept; Steam is asked only for what is
        // still missing after that.
        var kept = settling.IndexOf(
            "_settings.PlayerName = LocalIdentityService.NormalizedOrNothing(_settings.PlayerName);",
            StringComparison.Ordinal);
        var guard = settling.IndexOf("if (_settings.PlayerName.Length == 0)", StringComparison.Ordinal);
        var asked = settling.IndexOf(
            "LocalIdentityService.NicknameFromPersona(personaName)", StringComparison.Ordinal);
        Assert.True(kept >= 0, "The stored name is no longer kept.");
        Assert.True(guard > kept, "Steam is asked before the stored name is looked at.");
        Assert.True(asked > guard, "The persona is taken without checking for a name first.");

        // And the persona that reaches it is the one Steam just gave.
        Assert.Contains(
            "ResolveAndPersistLocalIdentity(status.PersonaName);", window, StringComparison.Ordinal);

        // A game started with no name is a player nobody can address, so the
        // button waits rather than inventing one at the last moment.
        Assert.Contains("&& !_isEditingPlayerName && hasName;", window, StringComparison.Ordinal);
        Assert.Contains(
            "var hasName = !string.IsNullOrWhiteSpace(_settings.PlayerName);",
            window,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A name typed while Steam is still connecting has to be written down
    /// then and there, or the launcher forgets it the moment Steam answers.
    /// </summary>
    [Fact]
    public void ANameTypedBeforeSteamAnswers_IsStillSaved()
    {
        var window = File.ReadAllText(FindRepositoryFile("Program", "MainWindow.xaml.cs"));
        var body = Between(window, "private void PersistActivePlayerIdentity()", "\n    }");

        // The identity fields are filled only when Steam has bound, and the
        // save happens afterwards either way - outside that condition.
        var bound = body.IndexOf("_identityService is { IsBound: true }", StringComparison.Ordinal);
        var saved = body.LastIndexOf("_settingsService.Save(_settings);", StringComparison.Ordinal);
        Assert.True(bound >= 0, "The bound check is gone; the name may now be saved without one.");
        Assert.True(saved > bound, "The name is no longer saved after the identity check.");
        // And nothing turns back at the door for want of a name: the early
        // return above the identity check must not mention one.
        Assert.DoesNotContain("PlayerName", body[..bound], StringComparison.Ordinal);
    }

    private static string Between(string text, string start, string end)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"\"{start}\" was not found.");
        var rest = text[from..];
        var to = rest.IndexOf(end, start.Length, StringComparison.Ordinal);
        return to < 0 ? rest : rest[..to];
    }

    private static string FindRepositoryDirectory(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (Directory.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException($"Repository folder was not found: {Path.Combine(relativeParts)}");
    }

    private static string FindRepositoryFile(params string[] relativeParts)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            var candidate = relativeParts.Aggregate(current.FullName, Path.Combine);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Repository file was not found: {Path.Combine(relativeParts)}");
    }
}
